using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WebSiteScraper
{
    class Program
    {
        private const int RetryCount = 5;

        private static IList<char> _BadChars;

        private static ConsoleColor DefaultConsoleColor = Console.ForegroundColor;

        private static int Delay = 10;

        public static void Main(string[] args)
        {
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Ssl3 | System.Net.SecurityProtocolType.Tls12;

            // download everything local to the site
            var site = args[0];
            var local = args[1];
            var update = args.Length > 2 ? bool.Parse(args[2]) : false;

            Task task;

            using (var client = new System.Net.Http.HttpClient())
            {
                var cancellationTokenSource = new System.Threading.CancellationTokenSource();
                task = Process(client, local, new Uri(site), update, cancellationTokenSource.Token);

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
            }

            if (task != null && task.IsFaulted)
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine("ERROR! - {0}", task.Exception.ToString());
                Console.ForegroundColor = DefaultConsoleColor;
            }
        }

        private static async Task<bool> Process(System.Net.Http.HttpClient client, string path, Uri uri, bool update, System.Threading.CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            Console.Write("Processing {0}", uri);
            var fileName = SanitizePath(Delimon.Win32.IO.Path.Combine(path, uri.Host) + uri.LocalPath.Replace(System.IO.Path.AltDirectorySeparatorChar, System.IO.Path.DirectorySeparatorChar), '_');
            if (!update && System.IO.File.Exists(fileName))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(" - Exists");
                Console.ForegroundColor = DefaultConsoleColor;
                return true;
            }

            // get the head
            using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, uri))
            {
                System.Net.Http.HttpResponseMessage response;

                try
                {
                    response = await client.SendAsync(request, cancellationToken);
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    WriteError(ex);
                    return false;
                }

                using (response)
                {
                    Console.ForegroundColor = response.StatusCode == System.Net.HttpStatusCode.OK ? ConsoleColor.DarkGreen : ConsoleColor.DarkRed;
                    Console.Write(" - {0}", response.StatusCode);
                    Console.ForegroundColor = DefaultConsoleColor;
                    if (response.StatusCode != System.Net.HttpStatusCode.OK)
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
                        await ProcessHtml(client, path, uri, update, cancellationToken);
                    }
                    else
                    {
                        // create the file name
                        var contentLength = response.Content.Headers.ContentLength;

                        // check to see if this exists
                        if (Delimon.Win32.IO.File.Exists(fileName))
                        {
                            var fileInfo = new Delimon.Win32.IO.FileInfo(fileName);
                            var fileLength = fileInfo.Length;

                            if (fileLength == contentLength)
                            {
                                return true;
                            }
                            else
                            {
                                // file is is not correct, continue with download
                            }
                        }

                        int count = 0;
                        while (!await ProcessLink(client, fileName, uri, contentLength, cancellationToken) && count < RetryCount)
                        {
                            count++;
                            System.Threading.Thread.Sleep(5000);
                        }

                        if (Delimon.Win32.IO.File.Exists(fileName))
                        {
                            var fileInfo = new Delimon.Win32.IO.FileInfo(fileName);
                            var fileLength = fileInfo.Length;

                            if (fileLength != contentLength)
                            {
                                WriteWarning("Invalid length, expected {0} - got {1}", contentLength, fileLength);
                                return false;
                            }
                        }
                    }
                }
            }

            return true;
        }

        private static async Task ProcessHtml(System.Net.Http.HttpClient client, string path, Uri uri, bool update, System.Threading.CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var html = await client.GetStringAsync(uri);
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
                    return;
                }

                for (int i = 0; i < nodes.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    var node = nodes[i];
                    // get the html attribute
                    var link = new Uri(uri, node.GetAttributeValue("href", string.Empty));
                    var relativeLink = link.MakeRelativeUri(uri);

                    if (relativeLink.IsAbsoluteUri)
                    {
                        continue;
                    }

                    var count = 0;
                    while (!await Process(client, path, link, update, cancellationToken) && count < RetryCount)
                    {
                        count++;
                        System.Threading.Thread.Sleep(5000);
                    }
                }
            }
        }

        private static async Task<bool> ProcessLink(System.Net.Http.HttpClient client, string fileName, Uri uri, long? expected, System.Threading.CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            Console.WriteLine("Downloading {0} bytes from {1}", expected, Delimon.Win32.IO.Path.GetFileName(fileName));
            var width = expected.ToString().Length;
            var bytes = new byte[1024];
            var webRequest = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(uri);
            webRequest.KeepAlive = true;
            webRequest.AllowAutoRedirect = false;
            webRequest.ProtocolVersion = System.Net.HttpVersion.Version10;
            webRequest.ServicePoint.ConnectionLimit = 24;
            webRequest.UserAgent = "Mozilla/4.5 (compatible; HTTrack 3.0x; Windows 98)";

            System.Net.WebResponse webResponse;
            try
            {
                webResponse = await webRequest.GetResponseAsync();
            }
            catch (System.Net.WebException ex)
            {
                WriteError(ex);
                return false;
            }

            using (webResponse)
            {
                var lastModified = DateTime.Parse(webResponse.Headers["Last-Modified"]);

                using (var stream = webResponse.GetResponseStream())
                {
                    // create the directory
                    System.IO.Directory.CreateDirectory(Delimon.Win32.IO.Path.GetDirectoryName(fileName));

                    using (var fileStream = Delimon.Win32.IO.File.Open(fileName, Delimon.Win32.IO.FileMode.Create, Delimon.Win32.IO.FileAccess.Write, Delimon.Win32.IO.FileShare.Read))
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
                                Console.CursorLeft = 0;
                                var position = fileStream.Position;
                                var positionChange = position - lastPosition;
                                lastPosition = position;
                                Console.Write("Downloaded  {0} bytes ({1:P}) at {2:0.00} KB/sec, with delay of {3}", position.ToString().PadLeft(width), (double)position / expected, positionChange / duration.TotalSeconds / 1024, Delay);
                                Console.Write("                    ");
                                Console.CursorLeft = 0;
                            }
                        }
                    }

                    // set the last modified date to what it should be
                    Delimon.Win32.IO.File.SetLastWriteTime(fileName, lastModified);
                }
            }

            return true;
        }

        private static void WriteError(Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine("ERROR! - {0}", ex.Message);
            Console.ForegroundColor = DefaultConsoleColor;
        }

        private static void WriteWarning(string format, params object[] args)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(format, args);
            Console.ForegroundColor = DefaultConsoleColor;
        }

        private static void WriteInformation(string message)
        {
            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.WriteLine(message);
            Console.ForegroundColor = DefaultConsoleColor;
        }

        private static string SanitizePath(string path, char replaceChar)
        {
            // construct a list of characters that can't show up in file names.
            // need to do this because ":" is not in InvalidPathChars
            if (_BadChars == null)
            {
                var badChars = new List<char>(Delimon.Win32.IO.Path.GetInvalidFileNameChars());
                badChars.AddRange(Delimon.Win32.IO.Path.GetInvalidPathChars());
                _BadChars = badChars.Distinct().ToList();
            }

            // remove root
            var root = Delimon.Win32.IO.Path.GetPathRoot(path);
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
