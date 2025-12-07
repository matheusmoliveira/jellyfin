using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers
{
    /// <summary>
    /// Controller for serving pre-generated HLS files (master.m3u8 and segments).
    /// </summary>
    [ApiController]
    [Route("HlsStatic")]
    [Authorize]
    public class HlsStaticController : BaseJellyfinApiController
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<HlsStaticController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="HlsStaticController"/> class.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="fileSystem">The file system abstraction.</param>
        /// <param name="logger">The logger.</param>
        public HlsStaticController(
            ILibraryManager libraryManager,
            IFileSystem fileSystem,
            ILogger<HlsStaticController> logger)
        {
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _logger = logger;
        }

        /// <summary>
        /// Serves a pre-generated HLS master playlist or segment file.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <param name="file">The file path relative to the HLS directory (e.g., "master.m3u8" or "1080p/segment001.ts").</param>
        /// <returns>The HLS file content.</returns>
        [HttpGet("{itemId}/{*file}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> GetHlsFile([FromRoute] Guid itemId, [FromRoute] string file)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is not Video video || string.IsNullOrEmpty(video.Path))
            {
                return NotFound();
            }

            // Find the master.m3u8 file
            var videoDirectory = Path.GetDirectoryName(video.Path);
            if (string.IsNullOrEmpty(videoDirectory))
            {
                return NotFound();
            }

            var masterM3u8Path = Path.Combine(videoDirectory, "master.m3u8");

            // Check parent directory
            if (!_fileSystem.FileExists(masterM3u8Path))
            {
                var parentDirectory = Directory.GetParent(videoDirectory)?.FullName;
                if (!string.IsNullOrEmpty(parentDirectory))
                {
                    var parentMasterM3u8 = Path.Combine(parentDirectory, "master.m3u8");
                    if (_fileSystem.FileExists(parentMasterM3u8))
                    {
                        masterM3u8Path = parentMasterM3u8;
                        videoDirectory = parentDirectory;
                    }
                }
            }

            if (!_fileSystem.FileExists(masterM3u8Path))
            {
                return NotFound();
            }

            // Build the full path to the requested file
            var hlsDirectory = Path.GetDirectoryName(masterM3u8Path);
            if (string.IsNullOrEmpty(hlsDirectory))
            {
                return NotFound();
            }

            var requestedFilePath = Path.Combine(hlsDirectory, file);

            // Security check: ensure the file is within the HLS directory
            var hlsDirectoryFullPath = Path.GetFullPath(hlsDirectory);
            var requestedFileFullPath = Path.GetFullPath(requestedFilePath);

            if (!requestedFileFullPath.StartsWith(hlsDirectoryFullPath, StringComparison.Ordinal))
            {
                return BadRequest("Invalid file path");
            }

            if (!_fileSystem.FileExists(requestedFilePath))
            {
                return NotFound();
            }

            // Determine content type
            var extension = Path.GetExtension(requestedFilePath).ToLowerInvariant();
            var contentType = extension switch
            {
                ".m3u8" => "application/x-mpegURL",
                ".ts" => "video/mp2t",
                ".mp4" => "video/mp4",
                ".m4s" => "video/mp4",
                _ => "application/octet-stream"
            };

            // If it's a master.m3u8 or variant playlist file, rewrite URLs to point to our controller
            if (extension == ".m3u8")
            {
                var masterContent = await System.IO.File.ReadAllTextAsync(requestedFilePath, cancellationToken: HttpContext.RequestAborted);

                // Rewrite relative URLs to point to our controller
                // Match lines that are not comments and contain file extensions
                var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/HlsStatic/{itemId}";
                var hlsDir = Path.GetDirectoryName(requestedFilePath);
                var hlsDirName = Path.GetFileName(hlsDir);

                // Rewrite relative paths (e.g., "1080p/playlist.m3u8" or "segment001.ts")
                var rewrittenContent = Regex.Replace(
                    masterContent,
                    @"^([^#\r\n]+\.(m3u8|ts|mp4|m4s))",
                    match =>
                    {
                        var relativePath = match.Groups[1].Value.Trim();
                        // If it's already an absolute URL, don't rewrite
                        if (Uri.TryCreate(relativePath, UriKind.Absolute, out _))
                        {
                            return match.Value;
                        }

                        // Make it relative to the HLS directory
                        return $"{baseUrl}/{relativePath}";
                    },
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);

                return Content(rewrittenContent, contentType);
            }

            return PhysicalFile(requestedFilePath, contentType, enableRangeProcessing: true);
        }
    }
}
