# AWS Deployment Guide — Large File Sorter

This document covers the full journey of deploying **Large File Sorter** to AWS using GitHub Actions, including every issue encountered and how it was resolved.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Prerequisites](#2-prerequisites)
3. [AWS Resources Setup (CloudShell)](#3-aws-resources-setup-cloudshell)
4. [IAM Roles & Permissions](#4-iam-roles--permissions)
5. [GitHub Repository Secrets](#5-github-repository-secrets)
6. [GitHub Actions Workflows](#6-github-actions-workflows)
7. [Dockerfiles](#7-dockerfiles)
8. [AWS Integration Test Script](#8-aws-integration-test-script)
9. [Issues & Resolutions](#9-issues--resolutions)
10. [Quick Reference — All CloudShell Commands](#10-quick-reference--all-cloudshell-commands)

---

## 1. Architecture Overview

```
GitHub Push (master)
        │
        ▼
┌───────────────────┐
│  GitHub Actions   │
│  (Ubuntu runner)  │
│                   │
│  1. Run tests     │
│  2. Build Docker  │
│  3. Push to ECR   │
│  4. Register task │
│  5. Run on ECS    │
└───────┬───────────┘
        │ docker push
        ▼
┌───────────────────┐        ┌───────────────────┐
│  Amazon ECR       │        │  Amazon S3        │
│  (image registry) │        │  (input / output) │
└───────────────────┘        └───────────────────┘
        │                             ▲  │
        │ pull image                  │  │ upload sorted
        ▼                             │  ▼
┌───────────────────────────────────────────────┐
│  Amazon ECS Fargate (large-file-sorter-cluster)│
│                                               │
│  Container: LargeFileGenerator + LargeFileSort│
│  [1] Generate test file                       │
│  [2] Upload input.txt → S3                    │
│  [3] Sort file                                │
│  [4] Upload sorted.txt → S3                   │
└───────────────────────────────────────────────┘
        │ stdout/stderr
        ▼
┌───────────────────┐
│  CloudWatch Logs  │
│  /ecs/large-file- │
│  sorter-test      │
└───────────────────┘
```

---

## 2. Prerequisites

| Tool | Version | Notes |
|------|---------|-------|
| .NET SDK | 10.0 | `net10.0` target framework |
| Docker | any | for local builds |
| AWS CLI | v2 | available in CloudShell |
| GitHub repo | — | with Actions enabled |

---

## 3. AWS Resources Setup (CloudShell)

All commands below were run from **AWS CloudShell** (`us-east-1`).

### 3.1 ECR Repositories

Two ECR repositories are needed: one for the sorter image (production deploy) and one for the integration test image.

```bash
# Production sorter image
aws ecr create-repository \
  --repository-name large-file-sorter \
  --region us-east-1

# Integration test image (generator + sorter + AWS CLI)
aws ecr create-repository \
  --repository-name large-file-sorter-test \
  --region us-east-1
```

Expected output for each:
```json
{
    "repository": {
        "repositoryArn": "arn:aws:ecr:us-east-1:155799260562:repository/large-file-sorter",
        "registryId": "155799260562",
        "repositoryUri": "155799260562.dkr.ecr.us-east-1.amazonaws.com/large-file-sorter",
        ...
    }
}
```

### 3.2 S3 Bucket

Used to store generated input files and sorted output.

```bash
aws s3api create-bucket \
  --bucket large-file-sorter-test-quantumhawk \
  --region us-east-1
```

> **Note:** bucket names must be globally unique and **all lowercase** — uppercased names cause `InvalidBucketName`.

### 3.3 ECS Cluster

```bash
aws ecs create-cluster \
  --cluster-name large-file-sorter-cluster \
  --region us-east-1
```

### 3.4 CloudWatch Log Group

```bash
aws logs create-log-group \
  --log-group-name /ecs/large-file-sorter-test \
  --region us-east-1

aws logs create-log-group \
  --log-group-name /ecs/large-file-sorter \
  --region us-east-1
```

> **Important:** If the log group does not exist before the task starts, the task fails immediately with `ResourceInitializationError` — see [Issue #4](#issue-4-task-fails-at-start-cloudwatch-log-group-missing).

### 3.5 ECS Task Definition (production)

```bash
aws ecs register-task-definition \
  --family large-file-sorter-task \
  --network-mode awsvpc \
  --requires-compatibilities FARGATE \
  --cpu 4096 \
  --memory 16384 \
  --execution-role-arn arn:aws:iam::155799260562:role/ecsTaskExecutionRole \
  --container-definitions '[{
    "name": "large-file-sorter",
    "image": "155799260562.dkr.ecr.us-east-1.amazonaws.com/large-file-sorter:latest",
    "essential": true,
    "logConfiguration": {
      "logDriver": "awslogs",
      "options": {
        "awslogs-group": "/ecs/large-file-sorter",
        "awslogs-region": "us-east-1",
        "awslogs-stream-prefix": "ecs"
      }
    }
  }]'
```

### 3.6 Subnets and Security Group

```bash
# List available subnets
aws ec2 describe-subnets \
  --query 'Subnets[*].[SubnetId,CidrBlock,AvailabilityZone]' \
  --output table

# List security groups
aws ec2 describe-security-groups \
  --query 'SecurityGroups[*].[GroupId,GroupName]' \
  --output table
```

Used values (default VPC):

| Resource | ID |
|----------|----|
| Subnet (any) | `subnet-08f87cece062a1297` (or others) |
| Security Group | `sg-0cf151616a2632ea0` (default) |

---

## 4. IAM Roles & Permissions

### 4.1 ecsTaskExecutionRole

Standard AWS-managed role. Allows ECS to pull images from ECR and write logs to CloudWatch.

**Policy attached:** `AmazonECSTaskExecutionRolePolicy`

If it does not exist, create it:
```bash
aws iam create-role \
  --role-name ecsTaskExecutionRole \
  --assume-role-policy-document '{
    "Version": "2012-10-17",
    "Statement": [{
      "Effect": "Allow",
      "Principal": {"Service": "ecs-tasks.amazonaws.com"},
      "Action": "sts:AssumeRole"
    }]
  }'

aws iam attach-role-policy \
  --role-name ecsTaskExecutionRole \
  --policy-arn arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy
```

### 4.2 ecsTaskRole

This is the role assumed **by the running container** — needed for S3 access.

```bash
# Create role (may already exist — EntityAlreadyExists error is safe to ignore)
aws iam create-role \
  --role-name ecsTaskRole \
  --assume-role-policy-document '{
    "Version": "2012-10-17",
    "Statement": [{
      "Effect": "Allow",
      "Principal": {"Service": "ecs-tasks.amazonaws.com"},
      "Action": "sts:AssumeRole"
    }]
  }'

# Attach S3 full access (or a custom policy for the specific bucket)
aws iam attach-role-policy \
  --role-name ecsTaskRole \
  --policy-arn arn:aws:iam::aws:policy/AmazonS3FullAccess
```

> **Issue:** Without this role, the `ecs register-task-definition` step fails with `Role is not valid` — see [Issue #5](#issue-5-register-task-definition-fails-role-is-not-valid).

### 4.3 github-actions-deployer IAM User

Used by GitHub Actions to authenticate to AWS. Permissions needed:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    { "Effect": "Allow", "Action": ["ecr:*"],       "Resource": "*" },
    { "Effect": "Allow", "Action": ["ecs:*"],       "Resource": "*" },
    { "Effect": "Allow", "Action": ["iam:PassRole"],"Resource": "*" },
    { "Effect": "Allow", "Action": ["logs:*"],      "Resource": "*" },
    { "Effect": "Allow", "Action": ["s3:*"],        "Resource": "*" }
  ]
}
```

> **Note:** The `github-actions-deployer` user did **not** originally have `s3:CreateBucket` — this caused S3 bucket creation from the workflow to fail. The bucket was then created manually via CloudShell — see [Issue #6](#issue-6-s3-createbucket-access-denied-in-github-actions).

---

## 5. GitHub Repository Secrets

Navigate to: **GitHub repo → Settings → Secrets and variables → Actions → New repository secret**

| Secret Name | Description | Where to get it |
|-------------|-------------|-----------------|
| `AWS_ACCESS_KEY_ID` | Access key for `github-actions-deployer` | IAM → Users → Security credentials |
| `AWS_SECRET_ACCESS_KEY` | Secret key for `github-actions-deployer` | IAM → Users → Security credentials (shown once at creation) |
| `AWS_ACCOUNT_ID` | 12-digit AWS account number | Top-right in AWS console, or `aws sts get-caller-identity --query Account` |
| `SUBNET_ID` | VPC subnet for Fargate tasks | `aws ec2 describe-subnets ...` |
| `SECURITY_GROUP_ID` | Security group for Fargate tasks | `aws ec2 describe-security-groups ...` |

> These are **Repository secrets** (not Environment secrets).

---

## 6. GitHub Actions Workflows

### 6.1 deploy.yml — Build, Test & Deploy

**Trigger:** push to `master`

**File:** `.github/workflows/deploy.yml`

**Steps:**
1. Run unit tests
2. Build Docker image using `Dockerfile`
3. Push image to ECR (`large-file-sorter` repository)
4. Download current ECS task definition
5. Render new task definition with updated image
6. Register new task definition revision
7. Run one-off Fargate task

### 6.2 aws-integration-test.yml — End-to-End AWS Test

**Trigger:** `workflow_dispatch` (manual trigger from GitHub UI)

**File:** `.github/workflows/aws-integration-test.yml`

**Inputs:**

| Input | Default | Description |
|-------|---------|-------------|
| `file_size_mb` | `1024` | Generated file size in MB |
| `chunk_size_mb` | `512` | Sorter chunk size in MB |
| `merge_fan_in` | `32` | Number of chunks merged at once |

**Steps:**
1. Build & push test image (`Dockerfile.awstest`) to ECR (`large-file-sorter-test`)
2. Ensure S3 bucket exists
3. Register ECS task definition with env vars for the test
4. Run Fargate task, wait for completion
5. Print CloudWatch logs
6. Print S3 output listing

---

## 7. Dockerfiles

### 7.1 Dockerfile (production sorter)

Multi-stage build:
- **Build stage**: `mcr.microsoft.com/dotnet/sdk:10.0` — restores, publishes `LargeFileSort` as self-contained `linux-x64` single-file binary
- **Runtime stage**: `mcr.microsoft.com/dotnet/runtime-deps:10.0` — minimal runtime, runs as non-root `sorteruser`

Key flags used in publish:
```
--self-contained true
/p:PublishSingleFile=true
/p:InvariantGlobalization=true   ← required to avoid libicu crash
```

### 7.2 Dockerfile.awstest (integration test image)

Multi-stage build:
- **Build stage**: publishes both `LargeFileSort` and `LargeFileGenerator` as self-contained binaries
- **Runtime stage**: `ubuntu:24.04` with AWS CLI v2 installed, copies both binaries + `aws-integration-test.sh`

Includes `libicu74` to satisfy .NET globalization requirement.

---

## 8. AWS Integration Test Script

**File:** `scripts/aws-integration-test.sh`

Environment variables (passed by ECS task definition):

| Variable | Default | Description |
|----------|---------|-------------|
| `S3_BUCKET` | `large-file-sorter-test` | S3 bucket name |
| `FILE_SIZE_MB` | `1024` | File size to generate |
| `CHUNK_SIZE_MB` | `512` | Sorter chunk size |
| `MERGE_FAN_IN` | `32` | Merge fan-in |

**Flow:**
```
[1/4] Generate FILE_SIZE_MB test file with LargeFileGenerator
[2/4] Upload input.txt → s3://$S3_BUCKET/input/input.txt
[3/4] Sort with LargeFileSort → sorted.txt
[4/4] Upload sorted.txt → s3://$S3_BUCKET/output/sorted.txt
Verify: compare line counts of input vs. sorted
```

---

## 9. Issues & Resolutions

### Issue #1: `NETSDK1047` — Assets file missing `linux-x64` target

**Error:**
```
error NETSDK1047: Assets file doesn't have a target for 'net10.0/linux-x64'.
Ensure that restore has run and that you have included 'linux-x64'
in your project's RuntimeIdentifiers.
```

**Cause:** `dotnet restore` was run without specifying the runtime identifier, so `project.assets.json` did not include a `linux-x64` target. Then `dotnet publish --no-restore -r linux-x64` failed.

**Fix:** Added a second restore with the explicit RID inside the Dockerfile before publish:
```dockerfile
RUN dotnet restore LargeFileSort/LargeFileSort.csproj -r linux-x64
RUN dotnet publish LargeFileSort/LargeFileSort.csproj \
    -c Release -o /app/publish \
    --no-restore -r linux-x64 --self-contained true \
    /p:PublishSingleFile=true /p:PublishTrimmed=false \
    /p:InvariantGlobalization=true
```

---

### Issue #2: AWS credentials not loaded in GitHub Actions

**Error:**
```
Credentials could not be loaded, please check your action inputs:
Could not load credentials from any providers
```

**Cause:** `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY` secrets were not configured in the GitHub repository.

**Fix:**
1. Create an IAM user `github-actions-deployer` with programmatic access.
2. Generate access key and secret key.
3. Add them as **Repository secrets** in GitHub:
   - Settings → Secrets and variables → Actions → New repository secret
   - `AWS_ACCESS_KEY_ID`
   - `AWS_SECRET_ACCESS_KEY`

---

### Issue #3: `.github/workflows` not tracked by git

**Cause:** The `.github/` directory was not added to git and was not pushed.

**Fix:**
```powershell
git add .github/workflows/deploy.yml
git add .github/workflows/aws-integration-test.yml
git commit -m "add github workflows"
git push
```

---

### Issue #4: Task fails at start — CloudWatch log group missing

**Error (from `aws ecs describe-tasks`):**
```
ResourceInitializationError: failed to validate logger args:
create stream has been retried 1 times:
failed to create Cloudwatch log group:
...logs:CreateLogGroup...is not authorized
```

**Cause (option A):** The `ecsTaskExecutionRole` lacked `logs:CreateLogGroup` permission.

**Cause (option B):** The log group didn't exist and auto-creation was blocked.

**Fix:** Pre-create the log groups in CloudShell:
```bash
aws logs create-log-group \
  --log-group-name /ecs/large-file-sorter-test \
  --region us-east-1
```

And add `"awslogs-create-group": "true"` to log configuration in the task definition so it can auto-create if needed.

---

### Issue #5: `register-task-definition` fails — Role is not valid

**Error:**
```
An error occurred (ClientException) when calling the RegisterTaskDefinition
operation: Role is not valid
```

**Cause:** `ecsTaskRole` did not exist or was referenced before being created.

**Fix:** Create the `ecsTaskRole` in CloudShell (see [Section 4.2](#42-ecstaskrole)):
```bash
aws iam create-role \
  --role-name ecsTaskRole \
  --assume-role-policy-document '{...}'

aws iam attach-role-policy \
  --role-name ecsTaskRole \
  --policy-arn arn:aws:iam::aws:policy/AmazonS3FullAccess
```

If the role already exists (EntityAlreadyExists), just ensure the policy is attached.

---

### Issue #6: S3 `CreateBucket` — Access Denied in GitHub Actions

**Error:**
```
An error occurred (AccessDenied) when calling the CreateBucket operation:
User: arn:aws:iam::155799260562:user/github-actions-deployer
is not authorized to perform: s3:CreateBucket
```

**Fix:** Created the bucket manually from CloudShell:
```bash
aws s3api create-bucket \
  --bucket large-file-sorter-test-quantumhawk \
  --region us-east-1
```

Then updated the workflow so it only checks existence and skips creation if the bucket is already there.

---

### Issue #7: `InvalidBucketName` — Uppercase letters in bucket name

**Error:**
```
An error occurred (InvalidBucketName) when calling the CreateBucket operation:
The specified bucket is not valid.
```

**Cause:** S3 bucket names must be all lowercase. The original name `large-file-sorter-test-QuantumHawk` contained uppercase letters.

**Fix:** Renamed to all-lowercase: `large-file-sorter-test-quantumhawk`.

---

### Issue #8: Container crash — `libicu` not found

**Error (from CloudWatch logs):**
```
Couldn't find a valid ICU package installed on the system.
Please install libicu (or icu-libs) using your package manager and try again.
Alternatively you can set the configuration flag System.Globalization.Invariant
to true if you want to run with no globalization support.
```

**Cause:** The .NET runtime requires ICU (International Components for Unicode) for globalization. The `alpine`-based or minimal images don't ship it. Also, the self-contained binary was built without the invariant globalization flag.

**Fix (two steps):**

1. Add `/p:InvariantGlobalization=true` to the publish command in both Dockerfiles.
2. For the test image (`Dockerfile.awstest`), which is based on `ubuntu:24.04`, also install `libicu74`:
```dockerfile
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl unzip ca-certificates libicu74 && ...
```

---

### Issue #9: Generator crash — `Invalid targetSizeMb`

**Error (from CloudWatch logs):**
```
Invalid targetSizeMb.
```

**Cause:** The shell script was calling:
```bash
/app/LargeFileGenerator "$INPUT_FILE" "$FILE_SIZE_MB"
```
But `LargeFileGenerator` expected the `--size-mb` flag, not a positional argument.

**Fix:** Updated the call in `aws-integration-test.sh` to match the CLI interface:
```bash
/app/LargeFileGenerator "$INPUT_FILE" --size-mb "$FILE_SIZE_MB"
```

---

### Issue #10: BOM character `﻿` in unit test file reads

**Error:**
```
Expected string length 3 but was 4. Strings differ at index 0.
Expected: < "aaa", "bbb", "ccc" >
But was:  < "﻿aaa", "bbb", "ccc" >
```

**Cause:** Test data strings were written with a UTF-8 BOM (`\uFEFF`), which was included in the first string read.

**Fix:** Opened test file streams with `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)` or used `Encoding.UTF8` (which strips BOM on read) consistently. Updated `BlockLineReader` to skip BOM bytes at the start of the file.

---

## 10. Quick Reference — All CloudShell Commands

```bash
# ── ECR ──────────────────────────────────────────────────────────────────────
aws ecr create-repository --repository-name large-file-sorter      --region us-east-1
aws ecr create-repository --repository-name large-file-sorter-test --region us-east-1

# ── S3 ───────────────────────────────────────────────────────────────────────
aws s3api create-bucket --bucket large-file-sorter-test-quantumhawk --region us-east-1

# ── ECS Cluster ──────────────────────────────────────────────────────────────
aws ecs create-cluster --cluster-name large-file-sorter-cluster --region us-east-1

# ── CloudWatch Log Groups ─────────────────────────────────────────────────────
aws logs create-log-group --log-group-name /ecs/large-file-sorter      --region us-east-1
aws logs create-log-group --log-group-name /ecs/large-file-sorter-test --region us-east-1

# ── IAM — ecsTaskExecutionRole ───────────────────────────────────────────────
aws iam create-role \
  --role-name ecsTaskExecutionRole \
  --assume-role-policy-document '{
    "Version":"2012-10-17",
    "Statement":[{"Effect":"Allow","Principal":{"Service":"ecs-tasks.amazonaws.com"},"Action":"sts:AssumeRole"}]
  }'
aws iam attach-role-policy \
  --role-name ecsTaskExecutionRole \
  --policy-arn arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy

# ── IAM — ecsTaskRole (for S3 access from the container) ─────────────────────
aws iam create-role \
  --role-name ecsTaskRole \
  --assume-role-policy-document '{
    "Version":"2012-10-17",
    "Statement":[{"Effect":"Allow","Principal":{"Service":"ecs-tasks.amazonaws.com"},"Action":"sts:AssumeRole"}]
  }'
aws iam attach-role-policy \
  --role-name ecsTaskRole \
  --policy-arn arn:aws:iam::aws:policy/AmazonS3FullAccess

# ── ECS Task Definition (production) ─────────────────────────────────────────
aws ecs register-task-definition \
  --family large-file-sorter-task \
  --network-mode awsvpc \
  --requires-compatibilities FARGATE \
  --cpu 4096 --memory 16384 \
  --execution-role-arn arn:aws:iam::155799260562:role/ecsTaskExecutionRole \
  --container-definitions '[{
    "name":"large-file-sorter",
    "image":"155799260562.dkr.ecr.us-east-1.amazonaws.com/large-file-sorter:latest",
    "essential":true,
    "logConfiguration":{
      "logDriver":"awslogs",
      "options":{
        "awslogs-group":"/ecs/large-file-sorter",
        "awslogs-region":"us-east-1",
        "awslogs-stream-prefix":"ecs"
      }
    }
  }]'

# ── Discover networking ───────────────────────────────────────────────────────
aws ec2 describe-subnets \
  --query 'Subnets[*].[SubnetId,CidrBlock,AvailabilityZone]' --output table

aws ec2 describe-security-groups \
  --query 'SecurityGroups[*].[GroupId,GroupName]' --output table

# ── Watch a running task ──────────────────────────────────────────────────────
TASK_ID="<your-task-id>"
aws ecs describe-tasks \
  --cluster large-file-sorter-cluster \
  --tasks $TASK_ID \
  --query 'tasks[0].{status:lastStatus,stopCode:stopCode,reason:stoppedReason}' \
  --output json

# ── Read CloudWatch logs ──────────────────────────────────────────────────────
aws logs get-log-events \
  --log-group-name /ecs/large-file-sorter-test \
  --log-stream-name "ecs/large-file-sorter-test/$TASK_ID" \
  --query 'events[*].message' --output text

# ── Check S3 output ───────────────────────────────────────────────────────────
aws s3 ls s3://large-file-sorter-test-quantumhawk/output/ --human-readable
```

---

## Resource Summary

| Resource | Name / ID |
|----------|-----------|
| AWS Region | `us-east-1` |
| ECR repo (sorter) | `large-file-sorter` |
| ECR repo (test) | `large-file-sorter-test` |
| S3 Bucket | `large-file-sorter-test-quantumhawk` |
| ECS Cluster | `large-file-sorter-cluster` |
| ECS Task Family | `large-file-sorter-task` |
| ECS Test Task Family | `large-file-sorter-integration-test` |
| Log Group (sorter) | `/ecs/large-file-sorter` |
| Log Group (test) | `/ecs/large-file-sorter-test` |
| IAM execution role | `ecsTaskExecutionRole` |
| IAM task role | `ecsTaskRole` |
| IAM deploy user | `github-actions-deployer` |
| Container name | `large-file-sorter` |
| Account ID | `155799260562` |

