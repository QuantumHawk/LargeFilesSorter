using System.Text;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

namespace LargeFileSorter
{
    internal sealed class S3Transport : IDisposable
    {
        private readonly IAmazonS3 _s3;

        public S3Transport(string region)
            => _s3 = new AmazonS3Client(RegionEndpoint.GetBySystemName(region));

        public async Task<long> GetFileSizeAsync(string bucket, string key)
        {
            var meta = await _s3.GetObjectMetadataAsync(bucket, key).ConfigureAwait(false);
            return meta.ContentLength;
        }

        /// <summary>
        /// Downloads bytes [nominalStart, nominalEnd] with 1 KB alignment buffers,
        /// trims to line boundaries, and writes lines to localPath.
        /// Non-first workers skip the partial first line.
        /// All workers include the line that crosses their nominal end.
        /// </summary>
        public async Task DownloadSliceToFileAsync(
            string bucket, string key,
            long nominalStart, long nominalEnd, long fileSize,
            int workerId, int workerCount, string localPath)
        {
            const int AlignBuf = 1024;
            long dlStart = workerId == 0 ? 0 : Math.Max(0, nominalStart - AlignBuf);
            long dlEnd   = Math.Min(fileSize - 1, nominalEnd + AlignBuf);

            using var response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket, Key = key,
                ByteRange  = new ByteRange(dlStart, dlEnd)
            }).ConfigureAwait(false);

            using var reader = new StreamReader(
                new BufferedStream(response.ResponseStream, 65536),
                Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 65536);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath)) ?? ".");
            await using var writer = new StreamWriter(
                localPath, append: false,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1 << 20);

            long absPos = dlStart;

            // Skip partial first line — it was already written by the previous worker.
            if (workerId > 0)
            {
                string? partial = await reader.ReadLineAsync().ConfigureAwait(false);
                if (partial != null)
                    absPos += Encoding.UTF8.GetByteCount(partial) + 1;
            }

            bool crossedEnd = false;
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                absPos += Encoding.UTF8.GetByteCount(line) + 1;

                // Non-last workers: stop after including the line that crosses nominalEnd.
                if (workerId < workerCount - 1)
                {
                    if (crossedEnd) break;
                    if (absPos > nominalEnd) crossedEnd = true;
                }
                // Last worker runs until actual EOF.
            }
        }

        public async Task UploadFileAsync(string bucket, string key, string localPath)
        {
            // TransferUtility automatically uses multipart upload for large files.
            // PutObjectAsync has a 5 GB single-part limit — parts can exceed that for 100 GB inputs.
            using var transfer = new TransferUtility(_s3);
            await transfer.UploadAsync(new TransferUtilityUploadRequest
            {
                BucketName = bucket,
                Key        = key,
                FilePath   = localPath,
                PartSize   = 64 * 1024 * 1024   // 64 MB parts
            }).ConfigureAwait(false);
        }

        public async Task DownloadFileAsync(string bucket, string key, string localPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath)) ?? ".");
            // TransferUtility handles large files correctly via ranged GET internally.
            using var transfer = new TransferUtility(_s3);
            await transfer.DownloadAsync(new TransferUtilityDownloadRequest
            {
                BucketName = bucket,
                Key        = key,
                FilePath   = localPath
            }).ConfigureAwait(false);
        }

        public async Task<List<string>> ListKeysAsync(string bucket, string prefix)
        {
            var keys  = new List<string>();
            string? token = null;
            do
            {
                var resp = await _s3.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucket, Prefix = prefix, ContinuationToken = token
                }).ConfigureAwait(false);
                keys.AddRange(resp.S3Objects.Select(o => o.Key));
                token = resp.IsTruncated ? resp.NextContinuationToken : null;
            } while (token != null);
            return keys;
        }

        public void Dispose() => _s3.Dispose();
    }
}
