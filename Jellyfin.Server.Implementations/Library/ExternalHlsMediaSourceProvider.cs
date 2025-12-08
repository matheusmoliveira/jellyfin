using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Library
{
    /// <summary>
    /// Media source provider for external HLS files (master.m3u8) accessed via HTTP URLs.
    /// Detects if a video item has an IMDb ID and creates an external HLS media source.
    /// </summary>
    public class ExternalHlsMediaSourceProvider : IMediaSourceProvider
    {
        private const string SlugProviderKey = "HlsSlug";
        private readonly ILogger<ExternalHlsMediaSourceProvider> _logger;
        private readonly IServerApplicationHost _appHost;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExternalHlsMediaSourceProvider"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="appHost">Application host.</param>
        public ExternalHlsMediaSourceProvider(
            ILogger<ExternalHlsMediaSourceProvider> logger,
            IServerApplicationHost appHost)
        {
            _logger = logger;
            _appHost = appHost;
        }

        /// <inheritdoc />
        public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            // Only process Video items
            if (item is not Video video)
            {
                return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
            }

            // Check if this is a .strm file with an HTTP URL (external HLS stream)
            if (!string.IsNullOrEmpty(video.Path) && video.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                // Check if the .strm file contains an HTTP URL
                if (!string.IsNullOrEmpty(video.ShortcutPath) &&
                    (video.ShortcutPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     video.ShortcutPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    // This is a .strm file with an external HTTP URL
                    // Create a MediaSourceInfo that marks SupportsDirectPlay = true for HTTP URLs
                    _logger.LogInformation("Found .strm file with external HTTP URL for item {ItemName} (ID: {ItemId}): {Url}", video.Name, video.Id, video.ShortcutPath);
                    var strmMediaSource = new MediaSourceInfo
                    {
                        Id = video.Id.ToString("N"), // Use the same ID as the item to ensure it's selected
                        Path = video.ShortcutPath, // Use the HTTP URL from .strm file
                        Protocol = MediaProtocol.Http,
                        Container = video.ShortcutPath.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? "m3u8" : null,
                        SupportsDirectPlay = true, // Mark as supporting direct play for HTTP URLs
                        SupportsDirectStream = false,
                        SupportsTranscoding = false,
                        IsRemote = true,
                        Type = MediaSourceType.Default,
                        Name = "External Stream",
                        MediaStreams = video.ShortcutPath.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                            ? new MediaStream[]
                            {
                                new MediaStream
                                {
                                    Type = MediaStreamType.Video,
                                    Index = -1,
                                    Codec = "h264"
                                },
                                new MediaStream
                                {
                                    Type = MediaStreamType.Audio,
                                    Index = -1,
                                    Codec = "aac"
                                }
                            }
                            : Array.Empty<MediaStream>()
                    };

                    return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { strmMediaSource });
                }
            }

            // Check if the item has an IMDb ID
            if (!video.TryGetProviderId(MetadataProvider.Imdb, out var imdbId) || string.IsNullOrEmpty(imdbId))
            {
                return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
            }

            // If the item has a physical path, skip external HLS (use physical file instead)
            // Only use external HLS if there's no physical file path
            if (!string.IsNullOrEmpty(video.Path))
            {
                return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
            }

            _logger.LogInformation("Found external HLS source for item {ItemName} with IMDb ID {ImdbId}", video.Name, imdbId);

            // Create URL to serve HLS through our custom controller
            var baseUrl = _appHost.GetApiUrlForLocalAccess();
            var folderKey = BuildFolderKey(video, imdbId);
            var hlsUrl = $"{baseUrl}/HlsExternal/{folderKey}/master.m3u8";

            // Create MediaSourceInfo for the external HLS file
            var mediaSource = new MediaSourceInfo
            {
                Id = $"external-hls-{folderKey}",
                Path = hlsUrl, // Use HTTP URL to serve through our controller
                Protocol = MediaProtocol.Http,
                Container = "m3u8",
                SupportsDirectPlay = true,
                SupportsDirectStream = false,
                SupportsTranscoding = false, // External HLS, no transcoding needed
                IsRemote = true,
                Type = MediaSourceType.Default,
                Name = "HLS (External)",
                MediaStreams = new MediaStream[]
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Index = -1,
                        // HLS typically uses H.264
                        Codec = "h264"
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Index = -1,
                        // HLS typically uses AAC
                        Codec = "aac"
                    }
                },
                Bitrate = 8000000 // Default bitrate, can be adjusted
            };

            return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { mediaSource });
        }

        /// <inheritdoc />
        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            // External HLS files don't need opening, they're remote URLs
            throw new NotImplementedException("External HLS files are remote URLs and don't require opening");
        }

        private static string BuildFolderKey(Video video, string imdbId)
        {
            if (video.ProviderIds.TryGetValue(SlugProviderKey, out var explicitSlug) && !string.IsNullOrWhiteSpace(explicitSlug))
            {
                return explicitSlug;
            }

            var nameSource = video.Name;

            if (string.IsNullOrWhiteSpace(nameSource) && !string.IsNullOrEmpty(video.Path))
            {
                try
                {
                    nameSource = Path.GetFileName(Path.GetDirectoryName(video.Path));
                }
                catch
                {
                    nameSource = null;
                }
            }

            if (string.IsNullOrWhiteSpace(nameSource))
            {
                return imdbId;
            }

            var normalized = nameSource.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            var slug = Regex.Replace(builder.ToString().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

            if (string.IsNullOrEmpty(slug))
            {
                return imdbId;
            }

            return $"{slug}-imdbid-{imdbId}".ToLowerInvariant();
        }
    }
}
