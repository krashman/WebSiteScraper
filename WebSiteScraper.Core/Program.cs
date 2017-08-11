namespace WebSiteScraper
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    internal class Program
    {
        private const int RetryCount = 5;

        private static IList<char> _BadChars;

        private static ConsoleColor DefaultConsoleColor = Console.ForegroundColor;

        private static int Delay = 10;

        private static bool update;

        private static string currentIndent = null;

        private static int currentLeft = 0;

        static int Main(string[] args)
        {
            if (System.Console.BufferWidth == System.Console.WindowWidth)
            {
                System.Console.SetBufferSize(System.Console.BufferWidth * 2, System.Console.BufferHeight);
            }

            // download everything local to the site
            var commandLineApplication = new Microsoft.Extensions.CommandLineUtils.CommandLineApplication();
            var siteArgument =  commandLineApplication.Argument("site", "The site to scrape");
            var folderArgument =  commandLineApplication.Argument("folder", "The folder to scrape to");
            var updateOption =  commandLineApplication.Option("-u|--update", "Whether to update or ignore existing files", Microsoft.Extensions.CommandLineUtils.CommandOptionType.NoValue);
            
            commandLineApplication.HelpOption("-h|--help");
            commandLineApplication.OnExecute(() =>
                {
                    var site = siteArgument.Value;
                    var local = folderArgument.Value;
                    update = updateOption.HasValue();

                     Task task;

                    using (var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None }))
                    {
                        var cancellationTokenSource = new System.Threading.CancellationTokenSource();
                        var baseUri = new Uri(site);
                        var basePath = GetFileName(local, baseUri);

                        if (!update && System.IO.File.Exists(basePath))
                        {
                            // just use the current
                            var links = GetLinks(System.IO.File.ReadAllText(basePath), baseUri);
                            task = ProcessUris(client, local, update ? links : links.OrderBy(_ => System.IO.File.Exists(GetFileName(local, _)), new ExistsComparer()), cancellationTokenSource.Token);
                        }
                        else
                        {
                            task = Process(client, local, baseUri, cancellationTokenSource.Token);
                        }

                        while (!(task.IsFaulted || task.IsCompleted || task.IsCanceled))
                        {
                            System.Threading.Thread.Sleep(100);

                            if (Console.KeyAvailable)
                            {
                                var key = Console.ReadKey();
                                switch (key.Key)
                                {
                                    case ConsoleKey.Escape:
                                        cancellationTokenSource.Cancel();
                                        break;
                                    case ConsoleKey.UpArrow:
                                        Delay++;
                                        break;
                                    case ConsoleKey.DownArrow:
                                        if (Delay > 0)
                                        {
                                            Delay--;
                                        }

                                        break;
                                }                        
                            }
                        }

                        if (task != null && task.IsFaulted)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkRed;
                            Console.WriteLine("{0}ERROR! - {1}", currentIndent, task.Exception.ToString());
                            Console.ForegroundColor = DefaultConsoleColor;
                            return task.Exception.HResult;
                        }
                    }

                    return 0;
                });
            
            return commandLineApplication.Execute(args);
        }

        private static async Task<bool> Process(System.Net.Http.HttpClient client, string path, Uri uri, System.Threading.CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            Console.Write("Processing {0}", uri);
            var fileName = GetFileName(path, uri);
            if (!update && System.IO.File.Exists(fileName))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(" - Exists");
                Console.ForegroundColor = DefaultConsoleColor;
                return true;
            }

            // get the head
            IEnumerable<Uri> links;
            using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, uri))
            {
                System.Net.Http.HttpResponseMessage response;
                if (update)
                {
                    var fileInfo = new System.IO.FileInfo(fileName);
                    var modifiedDate = fileInfo.LastWriteTimeUtc;
                    request.Headers.IfModifiedSince = modifiedDate;
                }
                
                try
                {
                    response = await client.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    WriteError(ex);
                    return false;
                }

                using (response)
                {
                    Console.ForegroundColor = response.IsSuccessStatusCode ? ConsoleColor.Green : ConsoleColor.DarkRed;
                    Console.Write(" - {0}", response.StatusCode);
                    Console.ForegroundColor = DefaultConsoleColor;
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine();
                        return true;
                    }

                    if (uri != request.RequestUri)
                    {
                        WriteInformation(" - redirect detected");
                        return true;
                    }

                    if (response.Content == null)
                    {
                        WriteInformation(" - no content detected");
                        return true;
                    }

                    Console.WriteLine();
                    if (response.Content.Headers.ContentType != null && response.Content.Headers.ContentType.MediaType == "text/html")
                    {
                        // process this as a HTML page
                        links = await ProcessHtml(client, path, fileName, uri, cancellationToken);
                    }
                    else
                    {
                        // create the file name
                        var contentLength = response.Content.Headers.ContentLength;
                        System.Diagnostics.Debug.Assert(contentLength.HasValue && contentLength.Value > 0);

                        // check to see if this exists
                        if (System.IO.File.Exists(fileName))
                        {
                            var fileInfo = new System.IO.FileInfo(fileName);
                            var fileLength = fileInfo.Length;

                            if (fileLength == contentLength)
                            {
                                // check the date/time
                                if (response.Content.Headers.Contains("Last-Modified"))
                                {
                                    var lastModified = DateTime.Parse(response.Content.Headers.GetValues("Last-Modified").First());
                                    if (fileInfo.LastWriteTime != lastModified)
                                    {
                                        fileInfo.LastWriteTime = lastModified;
                                        fileInfo.Refresh();
                                    }
                                }

                                return true;
                            }
                        }

                        int count = 0;
                        while (!await ProcessLink(client, fileName, uri, contentLength, cancellationToken) && count < RetryCount)
                        {
                            count++;
                            System.Threading.Thread.Sleep(5000);
                        }

                        if (System.IO.File.Exists(fileName) && contentLength.HasValue)
                        {
                            var fileInfo = new System.IO.FileInfo(fileName);
                            var fileLength = fileInfo.Length;

                            if (fileLength != contentLength)
                            {
                                WriteWarning("{0}Invalid length, expected {1} - got {2}", currentIndent, contentLength, fileLength);
                                return false;
                            }
                        }

                        return true;
                    }
                }
            }

            if (links != null)
            {
                await ProcessUris(client, path, links, cancellationToken);
            }

            return true;
        }

        private static async Task<IEnumerable<Uri>> ProcessHtml(System.Net.Http.HttpClient client, string path, string fileName, Uri uri, System.Threading.CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var html = await client.GetStringAsync(uri);

            // create the directory
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fileName));
            
            // save the HTML
            System.IO.File.WriteAllText(fileName, html);

            return GetLinks(html, uri);
        }

        private static IEnumerable<Uri> GetLinks(string html, Uri uri)
        {
            var document = new HtmlAgilityPack.HtmlDocument();
            document.LoadHtml(html);

            if (document.DocumentNode != null)
            {
                // get all the anchor nodes
                var nodes = document.DocumentNode.SelectNodes("//a");

                int nodeCount = -1;
                try
                {
                    nodeCount = nodes.Count;
                }
                catch (NullReferenceException)
                {
                    return Enumerable.Empty<Uri>();
                }

                var values = new List<Uri>();
                for (int i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    // get the html attribute
                    var link = new Uri(uri, node.GetAttributeValue("href", string.Empty));
                    var relativeLink = link.MakeRelativeUri(uri);

                    if (relativeLink.IsAbsoluteUri)
                    {
                        continue;
                    }

                    values.Add(link);
                }

                return values;
            }

            return Enumerable.Empty<Uri>();
        }

        private static async Task ProcessUris(System.Net.Http.HttpClient client, string path, IEnumerable<Uri> links, System.Threading.CancellationToken cancellationToken)
        {
            // increase the indent
            currentIndent = currentIndent == null ? string.Empty : currentIndent + ' ';

            var linksList = links.ToList();
            for (var i = 0; i < linksList.Count; i++)
            {
                var link = linksList[i];
                Console.Write("{0}({1:P}) ", currentIndent, (double)(i + 1) / linksList.Count);
                currentLeft = Console.CursorLeft;

                var count = 0;
                while (!await Process(client, path, link, cancellationToken) && count < RetryCount)
                {
                    count++;
                    System.Threading.Thread.Sleep(5000);
                }
            }

            // decrease the indent
            if (currentIndent != null)
            {
                currentIndent = currentIndent.Length == 1 ? string.Empty : currentIndent.Substring(0, currentIndent.Length - 1);
            }
        }

        private static async Task<bool> ProcessLink(System.Net.Http.HttpClient client, string fileName, Uri uri, long? expected, System.Threading.CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            Console.CursorLeft = currentLeft;
            Console.Write("Downloading {0}", System.IO.Path.GetFileName(fileName));
            var bytes = new byte[1024];
            var webRequest = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(uri);
            webRequest.Headers[System.Net.HttpRequestHeader.UserAgent] = "Mozilla/4.5 (compatible; HTTrack 3.0x; Windows 98)";

            System.Net.WebResponse webResponse;
            try
            {
                webResponse = await webRequest.GetResponseAsync();
            }
            catch (System.Net.WebException ex)
            {
                Console.WriteLine();
                WriteError(ex);
                return false;
            }

            using (webResponse)
            {
                if (expected == null)
                {
                    expected = webResponse.ContentLength;
                }

                // divisor
                var divisor = 1;
                while ((expected / divisor) > 1024)
                {
                    divisor *= 1024;
                }

                var units = "B";
                switch (divisor)
                {
                    case 1024:
                        units = "KB";
                        break;
                    case 1048576:
                        units = "MB";
                        break;
                    case 1073741824:
                        units = "GB";
                        break;
                }

                Console.CursorLeft = currentLeft;
                var sizeFormat = "{0}";
                if (divisor > 1)
                {
                    sizeFormat = "{0:0.0}";
                }

                var expectedText = string.Format(sizeFormat, (double)expected / divisor);
                Console.WriteLine("Downloading {0} {1} from {2}", expectedText, units, System.IO.Path.GetFileName(fileName));
                var width = expectedText.Length;
                var lastModified = DateTime.Parse(webResponse.Headers["Last-Modified"]);

                using (var stream = webResponse.GetResponseStream())
                {
                    // create the directory
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fileName));

                    using (var fileStream = System.IO.File.Open(fileName, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read))
                    {
                        var lastDateTime = DateTime.Now;
                        long lastPosition = 0;

                        while (true)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                throw new OperationCanceledException(cancellationToken);
                            }

                            int count;
                            try
                            {
                                count = await stream.ReadAsync(bytes, 0, bytes.Length, cancellationToken);
                            }
                            catch (System.IO.IOException ex)
                            {
                                WriteError(ex);
                                return false;
                            }

                            if (count == 0)
                            {
                                break;
                            }

                            await fileStream.WriteAsync(bytes, 0, count, cancellationToken);
                            System.Threading.Thread.Sleep(Delay);

                            var now = DateTime.Now;
                            if ((now - lastDateTime).TotalMilliseconds > 1000)
                            {
                                var duration = now - lastDateTime;
                                lastDateTime = now;
                                await fileStream.FlushAsync(cancellationToken);
                                
                                var position = fileStream.Position;
                                var positionChange = position - lastPosition;
                                lastPosition = position;

                                Console.CursorLeft = currentLeft;
                                var positionValue = string.Format(sizeFormat, (double)position / divisor);
                                Console.Write("Downloaded  {0} {1} ({2:P}) at {3:0.00} KB/sec, with delay of {4}", positionValue.PadLeft(width), units, (double)position / expected, positionChange / duration.TotalSeconds / 1024, Delay);
                                var dataLeft = Console.BufferWidth - Console.CursorLeft - 1;
                                Console.Write(new string(' ', dataLeft));
                                Console.CursorLeft = 0;
                            }
                        }
                    }

                    // set the last modified date to what it should be
                    System.IO.File.SetLastWriteTime(fileName, lastModified);
                }
            }

            return true;
        }

        private static void WriteError(Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine("{0}ERROR! - {1}", currentIndent, ex.Message);
            Console.ForegroundColor = DefaultConsoleColor;
        }

        private static void WriteWarning(string format, params object[] args)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write(currentIndent);
            Console.WriteLine(format, args);
            Console.ForegroundColor = DefaultConsoleColor;
        }

        private static void WriteInformation(string message)
        {
            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.WriteLine(message);
            Console.ForegroundColor = DefaultConsoleColor;
        }

        private static string GetFileName(string path, Uri uri)
        {
            var sanitizedPath = SanitizePath(System.IO.Path.Combine(path, uri.Host) + uri.LocalPath.Replace(System.IO.Path.AltDirectorySeparatorChar, System.IO.Path.DirectorySeparatorChar), '_');
            return uri.LocalPath.EndsWith("/") ? System.IO.Path.Combine(sanitizedPath, "index.html"): sanitizedPath;
        }

        private static string SanitizePath(string path, char replaceChar)
        {
            // construct a list of characters that can't show up in file names.
            // need to do this because ":" is not in InvalidPathChars
            if (_BadChars == null)
            {
                var badChars = new List<char>(System.IO.Path.GetInvalidFileNameChars());
                badChars.AddRange(System.IO.Path.GetInvalidPathChars());
                _BadChars = badChars.Distinct().ToList();
            }

            // remove root
            var root = System.IO.Path.GetPathRoot(path);
            path = path.Remove(0, root.Length);

            // split on the directory separator character. Need to do this
            // because the separator is not valid in a file name.
            var parts = path.Split(System.IO.Path.DirectorySeparatorChar);

            // check each part to make sure it is valid.
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = _BadChars.Aggregate(parts[i], (current, c) => current.Replace(c, replaceChar));
            }

            return root + System.IO.Path.Combine(parts);
        }
    }
}
