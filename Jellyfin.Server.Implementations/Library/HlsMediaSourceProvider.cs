using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Library
{
    /// <summary>
    /// Media source provider for pre-generated HLS files (master.m3u8).
    /// Detects master.m3u8 files in the same directory as the video item and serves them directly.
    /// </summary>
    public class HlsMediaSourceProvider : IMediaSourceProvider
    {
        private readonly ILogger<HlsMediaSourceProvider> _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IServerApplicationHost _appHost;

        /// <summary>
        /// Initializes a new instance of the <see cref="HlsMediaSourceProvider"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="fileSystem">File system abstraction.</param>
        /// <param name="appHost">Application host.</param>
        public HlsMediaSourceProvider(
            ILogger<HlsMediaSourceProvider> logger,
            IFileSystem fileSystem,
            IServerApplicationHost appHost)
        {
            _logger = logger;
            _fileSystem = fileSystem;
            _appHost = appHost;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            // Only process Video items
            if (item is not Video video)
            {
                return Enumerable.Empty<MediaSourceInfo>();
            }

            // Check if the item has a path
            if (string.IsNullOrEmpty(video.Path))
            {
                return Enumerable.Empty<MediaSourceInfo>();
            }

            // Get the directory containing the video file
            var videoDirectory = Path.GetDirectoryName(video.Path);
            if (string.IsNullOrEmpty(videoDirectory))
            {
                return Enumerable.Empty<MediaSourceInfo>();
            }

            // Look for master.m3u8 in the same directory or parent directory
            // Structure: /srv/hls/MeuFilme/master.m3u8 or /srv/hls/MeuFilme/1080p/video.mkv
            var masterM3u8Path = Path.Combine(videoDirectory, "master.m3u8");

            // Check parent directory (for structure like /srv/hls/MeuFilme/master.m3u8)
            // when video is in /srv/hls/MeuFilme/1080p/video.mkv
            if (!_fileSystem.FileExists(masterM3u8Path))
            {
                var parentDirectory = Directory.GetParent(videoDirectory)?.FullName;
                if (!string.IsNullOrEmpty(parentDirectory))
                {
                    var parentMasterM3u8 = Path.Combine(parentDirectory, "master.m3u8");
                    if (_fileSystem.FileExists(parentMasterM3u8))
                    {
                        masterM3u8Path = parentMasterM3u8;
                    }
                }
            }

            // Also check if video is directly in a directory with master.m3u8
            // Structure: /srv/hls/MeuFilme/ where both video.mkv and master.m3u8 exist
            if (!_fileSystem.FileExists(masterM3u8Path))
            {
                var grandParentDirectory = Directory.GetParent(videoDirectory)?.Parent?.FullName;
                if (!string.IsNullOrEmpty(grandParentDirectory))
                {
                    var grandParentMasterM3u8 = Path.Combine(grandParentDirectory, Path.GetFileName(videoDirectory), "master.m3u8");
                    if (_fileSystem.FileExists(grandParentMasterM3u8))
                    {
                        masterM3u8Path = grandParentMasterM3u8;
                    }
                }
            }

            // Check if master.m3u8 exists
            if (!_fileSystem.FileExists(masterM3u8Path))
            {
                return Enumerable.Empty<MediaSourceInfo>();
            }

            _logger.LogInformation("Found pre-generated HLS master.m3u8 for item {ItemName} at {Path}", video.Name, masterM3u8Path);

            // Create URL to serve HLS through our custom controller
            var baseUrl = _appHost.GetApiUrlForLocalAccess();
            var hlsUrl = $"{baseUrl}/HlsStatic/{video.Id}/master.m3u8";

            // Create MediaSourceInfo for the HLS file
            var mediaSource = new MediaSourceInfo
            {
                Id = masterM3u8Path.GetMD5().ToString("N"),
                Path = hlsUrl, // Use HTTP URL to serve through our controller
                Protocol = MediaProtocol.Http,
                Container = "m3u8",
                SupportsDirectPlay = true,
                SupportsDirectStream = false,
                SupportsTranscoding = false, // Pre-generated HLS, no transcoding needed
                IsRemote = false,
                Type = MediaSourceType.Default,
                Name = "HLS (Pre-generated)",
                MediaStreams = new MediaStream[]
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Index = -1,
                        // We don't know the exact codec info from the m3u8, but HLS typically uses H.264
                        Codec = "h264"
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Index = -1,
                        // HLS typically uses AAC
                        Codec = "aac"
                    }
                }
            };

            // Try to infer bitrate from directory structure (e.g., /1080p/, /720p/)
            var directoryName = Path.GetFileName(videoDirectory);
            if (directoryName != null)
            {
                if (directoryName.Contains("1080p", StringComparison.OrdinalIgnoreCase))
                {
                    mediaSource.Bitrate = 8000000; // ~8 Mbps for 1080p
                }
                else if (directoryName.Contains("720p", StringComparison.OrdinalIgnoreCase))
                {
                    mediaSource.Bitrate = 4000000; // ~4 Mbps for 720p
                }
                else if (directoryName.Contains("480p", StringComparison.OrdinalIgnoreCase))
                {
                    mediaSource.Bitrate = 2000000; // ~2 Mbps for 480p
                }
                else if (directoryName.Contains("360p", StringComparison.OrdinalIgnoreCase))
                {
                    mediaSource.Bitrate = 1000000; // ~1 Mbps for 360p
                }
            }

            // If we couldn't determine bitrate from directory, try to read from master.m3u8
            if (!mediaSource.Bitrate.HasValue)
            {
                try
                {
                    var masterM3u8Content = await File.ReadAllTextAsync(masterM3u8Path, cancellationToken).ConfigureAwait(false);
                    // Look for BANDWIDTH in the master playlist
                    var bandwidthMatch = System.Text.RegularExpressions.Regex.Match(
                        masterM3u8Content,
                        @"BANDWIDTH=(\d+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (bandwidthMatch.Success && int.TryParse(bandwidthMatch.Groups[1].Value, out var bandwidth))
                    {
                        mediaSource.Bitrate = bandwidth;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read bitrate from master.m3u8: {Path}", masterM3u8Path);
                }
            }

            return new[] { mediaSource };
        }

        /// <inheritdoc />
        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            // Pre-generated HLS files don't need opening, they're static files
            throw new NotImplementedException("Pre-generated HLS files are static and don't require opening");
        }
    }
}
