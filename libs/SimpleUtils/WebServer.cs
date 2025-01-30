using System;
using System.Net;  // For Dns.GetHostEntry(), etc.
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;  // for GZIP (GZipStream class)
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


// See instructions on including this :  http://stackoverflow.com/questions/7000811/cannot-find-javascriptserializer-in-net-4-0 
// To add a ref on an Assembly in VS :  Project->Add Reference->Assemblies->Framework and set CHECKBOX for System.Web.Extensions ...
using System.Web.Script.Serialization;    // for JavaScriptSerializer


namespace SimpleUtils
{
    public class WebServer
    {
        private readonly HttpListener Listener = new HttpListener();
        private readonly Func<HttpListenerRequest, HttpListenerResponse, string, Dictionary<string, string>, string, bool> ClientHttpRequestHandler;

        //
        // This is an OPTIONAL SIMPLIFIED AJAX REQUEST HANDLER that the client can register for handling requests to the "/ajax" namespace.
        // It assumes a particular request format (CGI key-values string, either in the POST body or on the URL);
        // and the client's handler is expected to return the response as a DICTIONARY, which is JSON-ized and returned to the web client.
        //
        // (We define this type as a delegate, not Func<in, in, ..., out>, because Func<> doesn't allow for a ref parameter).
        //
        public delegate Dictionary<string, dynamic> ClientAjaxRequestHandlerType(Dictionary<string, string> paramsMap, Dictionary<string, string> httpHeadersMap, string sourceIpAddr, bool canGzip, ref byte[] altPreGZippedResponse);
        private ClientAjaxRequestHandlerType ClientAjaxRequestHandler = null;

        //
        // If the client opts to have this class handle HTTP requests for LOCAL FILES (via HandleLocalFileRequests() API, below) ,
        // then these record the configuration for which files to serve.
        // We then also CACHE IN MEMORY the CONTENTS of all previously served files.
        //
        private HashSet<string> ServedFileNames = null;                   // set to specific local filenames and relatives paths of served files
        private HashSet<string> ServedFileSubdirectories = null;          // set to subdirectories containing served files
        private HashSet<string> ServedFileExtensions = null;              // set to exclusive exensions that are served from the served subdirectories (e.g.  "js", "jpg", ... )
        private Dictionary<string, byte[]> ServedFilesContentsCacheMap = null;  // CACHE for served FILES
        private Dictionary<string, byte[]> ServedFilesGZIPPEDCacheMap = null;  // CACHE for served FILES, with bytes pre-GZIPPED for PERF
        private Object CacheLock = new Object();  // protects ServedFilesContentsCacheMap, ServedFilesGZIPPEDCacheMap


        private static JavaScriptSerializer JsonSerializer = new JavaScriptSerializer();

        private static int VerboseLevel = 0;


        /// <summary>
        /// WebServer - constructor
        /// </summary>
        /// <param name="clientHttpRequestHandler">HTTP request handler function, which handles the entire HTTP request and response</param>
        /// <param name="listenerHttpPort"></param>
        /// <param name="servedNamespaces">
        /// An array of namespace prefixes that the server responds to;  e.g.  [ "/", "/js/", "/images/", "/ajax/", ... ] ;
        /// this includes subdirectories containing served local FILES.
        /// </param>
        /// <param name="verboseLevel"></param>
        public WebServer(
            Func<HttpListenerRequest, HttpListenerResponse, string, Dictionary<string, string>, string, bool> clientHttpRequestHandler,
            int listenerHttpPort,         // e.g. 80
            string[] servedNamespaces,    // e.g. { "/", "/js/", "/images/", "/ajax/" }
            int verboseLevel)
        {
            VerboseLevel = verboseLevel;

            if (!HttpListener.IsSupported)
            {
                throw new NotSupportedException("SimpleUtils.WebServer: Needs Windows XP SP2, Server 2003 or later.");
            }
            else
            {
                if ((servedNamespaces == null) || (servedNamespaces.Length == 0))
                {
                    throw new ArgumentException("servedNamespaces");
                }
                else if (clientHttpRequestHandler == null)
                {
                    throw new ArgumentException("clientHttpRequestHandler");
                }
                else
                {
                    JsonSerializer.MaxJsonLength = Math.Max(JsonSerializer.MaxJsonLength, Int32.MaxValue);  // may need huge capacity for response (or intermediates debug dumps which are then truncated)

                    foreach (string ns in servedNamespaces)
                    {
                        string fullReqPrefix = string.Format("http://*:{0}{1}", listenerHttpPort, ns);  // e.g. "http://*:80/js/"  , to serve files under the local /js directory .
                        Listener.Prefixes.Add(fullReqPrefix);
                    }

                    ClientHttpRequestHandler = clientHttpRequestHandler;
                    Listener.Start();
                }
            }
        }


        /// <summary>
        /// Run - loop forever, listening for inbound HTTP requests and launching a thread to process each one
        /// </summary>
        public void Run()
        {
            int minWorkerThreads = 0, maxWorkerThreads = 0;
            int minCompletionPortThreads = 0, maxCompletionPortThreads = 0;
            ThreadPool.GetMinThreads(out minWorkerThreads, out minCompletionPortThreads);
            ThreadPool.GetMaxThreads(out maxWorkerThreads, out maxCompletionPortThreads);

            DebugPrint(0, "SimpleUtils.WebServer thread is running (min/max worker threads = {0}/{1}) ... ", minWorkerThreads, maxWorkerThreads);

            try
            {
                while (Listener.IsListening)
                {
                    //
                    // Wait on our HttpListener until a new HTTP request arrives.  This BLOCKS until a new request arrives.
                    //
                    HttpListenerContext requestResponseContext = Listener.GetContext();

                    int availWorkerThreads = 0, availCompletionPortThreads = 0;
                    ThreadPool.GetAvailableThreads(out availWorkerThreads, out availCompletionPortThreads);
                    DebugPrint(2, "SimpleUtils.WebServer.Run: got HTTP request ({0}/{1} worker threads available) ... ", availWorkerThreads, maxWorkerThreads);

                    //
                    // Queue a thread to process the request.
                    //
                    ThreadPool.QueueUserWorkItem(HttpRequestWaitCallback, requestResponseContext);
                }
            }
            catch (Exception e)
            {
                Diagnostics.DumpException(e);
            }
        }


        /// <summary>
        /// CALLBACK from the HttpListener when a new HTTP request arrives.  Called on a ThreadPool THREAD.
        ///
        /// It has compatible type with the WaitCallback delegate.
        ///   see: https://msdn.microsoft.com/en-us/library/system.threading.waitcallback(v=vs.110).aspx
        ///
        /// This is called on a ThreadPool thread.  requestResponseContextObj is a typeless object which we must cast to HttpListenerContext .
        ///
        /// </summary>
        /// <param name="listenerContextObj"></param>
        private void HttpRequestWaitCallback(object requestResponseContextObj)
        {
            HttpListenerContext requestResponseContext = requestResponseContextObj as HttpListenerContext;  // typecast the object to HttpListenerContext

            HttpListenerRequest request = requestResponseContext.Request;
            HttpListenerResponse response = requestResponseContext.Response;

            string localPath = "???";   // (define this out here, so exception handler has access to it)

            try
            {
                // Capture the localPath immediately, so we have it in case request times out and is disposed of (System.ObjectDisposedException)
                localPath = request.Url.LocalPath;

                if (VerboseLevel >= 2)
                {
                    string sourceIPAddr = request.RemoteEndPoint.Address.ToString();
                    DebugPrint(2, "SimpleUtils.WebServer.HttpRequestWaitCallback() : inbound request from {0}, localPath='{1}' ... ", sourceIPAddr, localPath);

                    // dump all HTTP headers in the request
                    Dictionary<string, string> httpHeadersMap = ParseHTTPRequestHeaders(request);
                    foreach (KeyValuePair<string, string> keyVal in httpHeadersMap)
                    {
                        DebugPrint(2, "   http header {0} = {1} ", keyVal.Key, keyVal.Value);
                    }
                }



                if (TryHandleLocalFileRequest(request, response))
                {
                    //
                    // This was a request for a LOCAL FILE, which (per the client's indication) we processed here.
                    // So don't call the client's handler.
                    //                                  
                }
                else if (TryHandleAjaxRequest(request, response))
                {
                    //
                    // This was an AJAX request (on the "/ajax" namespace), for which the client registered a special simplified handler; and the request was handled by it.
                    // So don't call the client's main HTTP request handler.
                    //
                }
                else
                {
                    //
                    // Get just the path part of the request;  e.g. "/foo" from "http://localhost:8080/foo?abc=def&xyz=3"
                    //
                    localPath = localPath.ToLower();
                    if ((localPath.Length > 1) && localPath.EndsWith("/"))
                    {
                        localPath = localPath.Substring(0, localPath.Length - 1);
                    }

                    //
                    // Get the request namespace; e.g. "foo" from "http://localhost:8080/foo?abc=def&xyz=3"
                    //
                    string[] parts = localPath.Split('/');
                    string requestNamespace = (parts.Length >= 2) ? parts[1] : "";

                    //
                    // RawUrl is like "/foo?abc=def&xyz=3".
                    // Parse the CGI params from the URL, into a key-values map, like {"abc":"def", "xyz":"3"}
                    // However, if there is a POST BODY, look for the CGI params in the POST BODY.
                    //
                    // TODO(ervinp): BUGBUG - frequently get System.ObjectDisposedException here, presumably because an AJAX request
                    //                        timed out and HttpListenerRequest was disposed of ???
                    //
                    byte[] postBodyBytes = (request.HttpMethod.ToUpper() == "POST") ? ReadHTTPRequestPOSTBodyAsBytes(request) : new byte[0];

                    //
                    // To convert the POST BODY to STRING, use NO ENCODING, to preserve binary data (like images)
                    // (do not use System.Text.Encoding.Default.GetString(), nor UTF8/Unicode/etc).
                    //
                    string postBodyStr = (postBodyBytes.Length > 0) ? new string(postBodyBytes.Select(b => (char)b).ToArray()) : "";

                    // Try to parse parameter key-values from the URL.  If there are none, and it's and HTTP POST request with a body, then try to parse them from the POST body.
                    Dictionary<string, string> cgiParamsMap = ParseRequestParamsFromUrl(request);
                    if ((cgiParamsMap.Count == 0) && (postBodyStr.Length > 0))
                    {
                        cgiParamsMap = ParseRequestParamsFromPOSTBody(postBodyStr);  // NOTE : this produces garbage if POST body data is binary (or not JSON)
                    }

                    //
                    // Calls the client's request-handler method.
                    // Pass both the HttpListenerRequest and HttpListenerResponse to the handler.
                    // All the request processing AND response configuration will be done there.
                    //
                    bool didHandle = ClientHttpRequestHandler(request, response, requestNamespace, cgiParamsMap, postBodyStr);

                    if (!didHandle)
                    {
                        //
                        // '/probe' GET request is presumably a LoadBalancer probe (by our convention),
                        // which fails continuously until the server instance is ready; so don't complain about it.
                        //
                        if (request.Url.LocalPath != "/probe")
                        {
                            DebugPrint(1, " SimpleUtils.WebServer : UNHANDLED {0} request for namespace '{1}' . ", request.HttpMethod.ToUpper(), request.Url.LocalPath);
                        }

                        //
                        // If this was an HTTP GET request for an unhandled namespace, then REDIRECT to the default "/" namespace.
                        // For unhandled POST requests, we just drop them.
                        //
                        if ((request.HttpMethod.ToUpper() == "GET") && (request.Url.LocalPath != "/") && (request.Url.LocalPath != "/probe"))
                        {
                            string host = request.Url.Host;   // e.g. acme.com
                            string portStr = (request.Url.Port != 80) ? String.Format(":{0}", request.Url.Port) : "";  // e.g. ":8080", only for non-default port
                            string redirectUrl = string.Format("http://{0}{1}/", host, portStr);  // e.g. "http://acme.com/"
                            DebugPrint(1, " REDIRECTING '{0}' request to '{1}' . ", request.Url.LocalPath, redirectUrl);
                            response.Redirect(redirectUrl);
                        }
                    }
                }

                DebugPrint(2, "< SimpleUtils.WebServer.HttpRequestWaitCallback() : localPath='{0}' . ", localPath);
            }
            catch (Exception e)
            {
                Console.WriteLine("Dumping EXCEPTION in HttpRequestWaitCallback() : localPath = '{0}' ... ", localPath);
                Diagnostics.DumpException(e);
            }
            finally
            {
                //
                // Always CLOSE the stream.
                // NOTE : I have seen this raise an EXCEPTION.  So make sure to handle it.
                //
                try
                {
                    response.OutputStream.Close();
                }
                catch (Exception e)
                {
                    Console.WriteLine("HttpRequestWaitCallback() : OutputStream.Close() raised an EXCEPTION : localPath = '{0}' ... ", localPath);
                    Diagnostics.DumpException(e);
                }
            }
        }


        /// <summary>
        /// Stop
        /// </summary>
        public void Stop()
        {
            Listener.Stop();
            Listener.Close();
        }


        /// <summary>
        /// HandleLocalFileRequests - instructs this class to handle future HTTP requests for LOCAL FILES
        ///
        /// If called, this class TAKES OVER the serving of all local files, as specified in the params.
        /// This includes adding the correct Content-Type HTTP HEADER in the response.
        ///
        /// This class will then also CACHE each requested file IN MEMORY, for faster serving.
        ///
        /// The handled requests for local files will NOT be passed to the client's handler function.
        ///
        /// The library then also listens for requests to "/flushcachedfiles", which causes it to flush the files cache
        /// (this lets you update CSS/Javascript files underneath the running server).
        ///
        /// TODO(ervinp): add support for GZIP; and then cache both gzipped and non-gzipped responses.
        ///
        /// </summary>
        /// <param name="specificFileNames"></param>
        /// <param name="subdirectories"></param>
        /// <param name="fileExtensions"></param>
        public void HandleLocalFileRequests(
            string[] specificFileNames,    // e.g.  [ "index.html", "favicon.ico", "js/myclientlib.js" ]
            string[] subdirectories,       // e.g.  [ "js", "img" ]
            string[] fileExtensions        // exclusive list of file extensions to serve from the subDirectories;  e.g.  [ "js", "jpg", "png", "bmp" ]
            )
        {

            ServedFileNames = new HashSet<string>();
            foreach (string filename in specificFileNames)
            {
                if (filename.Length > 0)
                {
                    ServedFileNames.Add(filename);
                }
            }

            ServedFileSubdirectories = new HashSet<string>();
            foreach (string subdir in subdirectories)
            {
                if (subdir.Length > 0)
                {
                    ServedFileSubdirectories.Add(subdir);
                }
            }

            ServedFileExtensions = new HashSet<string>();
            foreach (string ext in fileExtensions)
            {
                if (ext.Length > 0)
                {
                    ServedFileExtensions.Add(ext);
                }
            }

            ServedFilesContentsCacheMap = new Dictionary<string, byte[]>();
            ServedFilesGZIPPEDCacheMap = new Dictionary<string, byte[]>();
        }


        /// <summary>
        /// RegisterAjaxRequestHandler - register a handler for HTTP requests to the "/ajax" namespace.  The interface assumes a specific simplified behavior by the client.
        /// </summary>
        /// <param name="clientAjaxRequestHandler"></param>
        public void RegisterAjaxRequestHandler(ClientAjaxRequestHandlerType clientAjaxRequestHandler)
        {
            ClientAjaxRequestHandler = clientAjaxRequestHandler;
        }


        /// <summary>
        /// TryHandleLocalFileRequest - if the request is for a LOCAL FILE that the client instructed us to handle (via HandleLocalFileRequests() ), then handle it.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="response"></param>
        /// <returns>true iff request was handled</returns>
        private bool TryHandleLocalFileRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            bool wasHandled = false;

            try
            {

                //
                // Check if we are handling any local file requests at all on behalf of the client.
                //
                if (ServedFileNames != null)
                {
                    //
                    // Get just the path part of the request;  e.g. "/foo" from "http://localhost:8080/foo?abc=def&xyz=3"
                    //
                    string localPath = request.Url.LocalPath;
                    localPath = localPath.ToLower();
                    if ((localPath.Length > 1) && localPath.EndsWith("/"))
                    {
                        localPath = localPath.Substring(0, localPath.Length - 1);
                    }

                    if ((localPath.Length > 0) && (localPath[0] == '/'))
                    {
                        localPath = localPath.Substring(1);
                    }

                    //
                    // Get the PREFIX of the path.  If this is a file request, then the prefix may be the subdirectory; or the complete file name if it is in the root directory.
                    //
                    string[] parts = localPath.Split('/');
                    string pathPrefix = (parts.Length >= 2) ? parts[0] : localPath;

                    //
                    // Get the suffix (file extension) of the requested file, if any  (e.g. "jpg"  from "/images/blah.jpg" ).
                    //
                    string[] parts2 = localPath.Split('.');
                    string suffix = (parts2.Length >= 2) ? parts2[parts2.Length - 1] : "";

                    //
                    // Check if this is a file request that we are supposed to handle.
                    //
                    if (ServedFileNames.Contains(pathPrefix) ||
                        (ServedFileSubdirectories.Contains(pathPrefix) && ServedFileExtensions.Contains(suffix)))
                    {
                        //
                        // This *appears* to be a request for a local file, and one which we are supposed to handle.
                        // 'localPath' is the presumed local relative file path.
                        //

                        // Check the HTTP request headers to see if the web client ALLOWS the response to be GZIPPED.
                        bool canGzip = CanGZipHTTPResponse(request);

                        byte[] fileBytes = null;
                        byte[] gzippedBytes = null;

                        //
                        // Check if we have already CACHED the file contents.
                        //
                        lock (CacheLock)
                        {
                            if (canGzip && (ServedFilesGZIPPEDCacheMap != null) && ServedFilesGZIPPEDCacheMap.ContainsKey(localPath))
                            {
                                gzippedBytes = ServedFilesGZIPPEDCacheMap[localPath];

                                DebugPrint(2, " SimpleUtils.WebServer : request for local file '{0}' served from PRE-GZIPPED CACHE ({1} bytes). ", localPath, gzippedBytes.Length);
                            }
                            else if ((ServedFilesContentsCacheMap != null) && ServedFilesContentsCacheMap.ContainsKey(localPath))
                            {
                                fileBytes = ServedFilesContentsCacheMap[localPath];

                                DebugPrint(2, " SimpleUtils.WebServer : request for local file '{0}' served from CACHE ({1} bytes). ", localPath, fileBytes.Length);
                            }
                            else
                            {
                                //
                                // Try to read the file from local disk.
                                //
                                if (File.Exists(localPath))
                                {
                                    //
                                    // The file may be BINARY; so use File.ReadAllBytes(), not File.ReadAllText() .
                                    // (It's ok if the file is empty.  We will cache empty files as well, in order to avoid checking the disk.)
                                    //
                                    fileBytes = File.ReadAllBytes(localPath);

                                    // CACHE the file contents IN MEMORY for the next access.
                                    ServedFilesContentsCacheMap[localPath] = fileBytes;

                                    DebugPrint(2, "SimpleUtils.WebServer : first request for local file '{0}' handled and cached ({1} bytes). ", localPath, fileBytes.Length);
                                }
                                else
                                {
                                    //
                                    // The request matched the spec for a local file that we are supposed to handle.
                                    // But that local file doesn't exist.
                                    // We will pass on the request to the client handler (wasHandled == false).
                                    //
                                    DebugPrint(1, "SimpleUtils.WebServer : request for non-existent local file '{0}' . ", localPath);
                                }
                            }
                        }

                        if ((gzippedBytes != null) || (fileBytes != null))
                        {
                            //
                            // Check the HTTP request headers to see if the web client allows the response to be GZIPPED (compressed).
                            //
                            if ((gzippedBytes == null) && canGzip)
                            {
                                gzippedBytes = TryToGZIPResponse(request, fileBytes);
                                if (gzippedBytes != null)
                                {
                                    // CACHE the PRE-GZIPPED file contents IN MEMORY for the next access.
                                    lock (CacheLock)
                                    {
                                        ServedFilesGZIPPEDCacheMap[localPath] = gzippedBytes;
                                    }
                                    DebugPrint(2, "SimpleUtils.WebServer : file {0} GZIP-compressed from {1} to {2} bytes. ", localPath, fileBytes.Length, gzippedBytes.Length);
                                }
                            }
                            if (gzippedBytes != null)
                            {
                                fileBytes = gzippedBytes;
                                response.AppendHeader("Content-Encoding", "gzip");  // add an HTTP header indicating that the response is GZIPPED
                            }


                            //
                            // Set the Content-Type HEADER in the HTTP RESPONSE for some common file types.
                            //
                            switch (suffix)
                            {
                                case "js": response.ContentType = "application/javascript"; break;
                                case "css": response.ContentType = "text/css"; break;

                                case "jpg": response.ContentType = "image/jpeg"; break;
                                case "bmp": response.ContentType = "image/bmp"; break;
                                case "png": response.ContentType = "image/png"; break;
                                case "gif": response.ContentType = "image/gif"; break;
                                case "tiff": response.ContentType = "image/tiff"; break;
                            }

                            //
                            // Send the HTTP RESPONSE.
                            //
                            response.ContentLength64 = fileBytes.Length;
                            response.OutputStream.Write(fileBytes, 0, fileBytes.Length);

                            wasHandled = true;
                        }
                    }
                    else if (pathPrefix == "flushcachedfiles")
                    {
                        //
                        // If this library is handling file requests, then it also responds to a GET request for "/flushcachedfiles" by flushing the file cache.
                        // This lets you update css/Javascript underneath the running server.
                        //
                        lock (CacheLock)
                        {
                            ServedFilesContentsCacheMap = new Dictionary<string, byte[]>();  // re-initialize the cache to an empty map
                            ServedFilesGZIPPEDCacheMap = new Dictionary<string, byte[]>();  // re-initialize the cache to an empty map
                        }

                        // simple HTML result for the sending browser
                        string htmlResponse = "<html><body>ok - FLUSHED SERVED FILES CACHE</body></html>";
                        response.ContentLength64 = htmlResponse.Length;
                        response.OutputStream.Write(Encoding.ASCII.GetBytes(htmlResponse), 0, htmlResponse.Length);

                        Console.WriteLine("SimpleUtils.WebServer : --- FLUSHED SERVED FILES CACHE ---");
                        wasHandled = true;
                    }
                }

            }
            catch (Exception e)
            {
                Diagnostics.DumpException(e);
                wasHandled = false;
            }

            return wasHandled;
        }


        /// <summary>
        /// TryHandleAjaxRequest - if the request url is for the "/ajax" namespace, and the client registered a special AJAX handler, then
        /// </summary>
        /// <param name="request"></param>
        /// <param name="response"></param>
        /// <returns>true iff request was handled</returns>
        private bool TryHandleAjaxRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            bool wasHandled = false;

            try
            {
                //
                // Did the client register an AJAX handler ?
                //
                if (ClientAjaxRequestHandler != null)
                {
                    //
                    // Get just the path part of the request;  e.g. "/foo" from "http://localhost:8080/foo?abc=def&xyz=3"
                    //
                    string localPath = request.Url.LocalPath;
                    localPath = localPath.ToLower();
                    if ((localPath.Length > 1) && localPath.EndsWith("/"))
                    {
                        localPath = localPath.Substring(0, localPath.Length - 1);
                    }

                    //
                    // Get the PREFIX of the path; e.g. "/ajax" -> "ajax"
                    //
                    string[] parts = localPath.Split('/');
                    string pathPrefix = (parts.Length >= 2) ? parts[1] : "";

                    //
                    // Is this an AJAX request, of the type that the client's custom handler will process ?
                    //
                    if (pathPrefix == "ajax")
                    {
                        // Get the requesting client's source IP address
                        string sourceIpAddr = request.RemoteEndPoint.Address.ToString();

                        Dictionary<string, string> httpHeadersMap = ParseHTTPRequestHeaders(request);

                        //
                        // RawUrl is like "/foo?abc=def&xyz=3".
                        // Parse the CGI params from the URL, into a key-values map, like {"abc":"def", "xyz":"3"}
                        // However, if there is a POST BODY, look for the CGI params in the POST BODY.
                        //
                        string postBody = (request.HttpMethod.ToUpper() == "POST") ? ReadHTTPRequestPOSTBody(request) : "";
                        Dictionary<string, string> paramsMap = (postBody.Length > 0) ?
                                                                ParseRequestParamsFromPOSTBody(postBody) :
                                                                ParseRequestParamsFromUrl(request);

                        string paramsStr = JsonSerializer.Serialize((object)paramsMap);
                        DebugPrint(1, "SimpleUtils.WebServer : AJAX request paramsMap = {0} ... ", paramsStr);

                        byte[] responseBytes = null;

                        //
                        // CALL the client's special AJAX REQUEST HANDLER.
                        //
                        bool canGzip = CanGZipHTTPResponse(request);
                        byte[] gzippedBytes = null;
                        Dictionary<string, dynamic> resultMap = ClientAjaxRequestHandler(paramsMap, httpHeadersMap, sourceIpAddr, canGzip, ref gzippedBytes);

                        if (resultMap != null)
                        {
                            // Did server return a pre-gzipped response for perf, but forget to return a null resultMap ?
                            if (gzippedBytes != null)
                            {
                                DebugPrint(0, "SimpleUtils.WebServer : WARNING: server returned gzipped bytes; but also a non-null results map; AJAX handler must return null for pre-gzipped response to be processed.");
                            }

                            //
                            // Convert the response to a JSON string, which will be the contents of the HTTP RESPONSE.
                            //
                            string jsonStr = JsonSerializer.Serialize((object)resultMap);
                            DebugPrint(1, "\nSimpleUtils.WebServer AJAX serialized JSON response = {0} bytes : resultsMap = {1} ... ", jsonStr.Length, jsonStr.Substring(0, Math.Min(jsonStr.Length, 200)));
                            responseBytes = Encoding.ASCII.GetBytes(jsonStr);

                            //
                            // Try to GZIPPED (compress) the response bytes (only if allowed by the HTTP request headers; and only if the result is smaller).
                            //
                            if (canGzip)
                            {
                                gzippedBytes = TryToGZIPResponse(request, responseBytes);
                                if (gzippedBytes != null)
                                {
                                    DebugPrint(1, "SimpleUtils.WebServer : AJAX response GZIP-compressed from {0} to {1} bytes. ", responseBytes.Length, gzippedBytes.Length);
                                    responseBytes = gzippedBytes;
                                    response.AppendHeader("Content-Encoding", "gzip");  // add an HTTP header indicating that the response is GZIPPED
                                }
                            }
                        }
                        else if (canGzip && (gzippedBytes != null))
                        {
                            //
                            // The client's AJAX request handler has returned a PRE-GZIPPED response, for perf.
                            //
                            DebugPrint(1, "SimpleUtils.WebServer : responding with server's PRE-GZIPPED AJAX response length {0}. ", gzippedBytes.Length);
                            responseBytes = gzippedBytes;
                            response.AppendHeader("Content-Encoding", "gzip");  // add an HTTP header indicating that the response is GZIPPED
                        }
                        else
                        {
                            // Did the server return a pre-gzipped buffer even though it's not allowed by the request ?
                            if (!canGzip && (gzippedBytes != null))
                            {
                                DebugPrint(0, " ERROR: SimpleUtils.WebServer-linked server returned pre-gzipped buffer even though canGzip was FALSE : paramsMap = {0} ... ", paramsStr);
                            }

                            DebugPrint(0, "\n ERROR: SimpleUtils.WebServer-linked server FAILED to handle AJAX request : paramsMap = {0} ... ", paramsStr);
                            resultMap = new Dictionary<string, dynamic>() { { "status_desc", "SimpleUtils.WebServer: server registered AJAX handler FAILED to handle request : " + paramsStr } };
                            string jsonStr = JsonSerializer.Serialize((object)resultMap);
                            responseBytes = Encoding.ASCII.GetBytes(jsonStr);
                        }

                        //
                        // Send the HTTP RESPONSE.
                        //
                        response.ContentLength64 = responseBytes.Length;
                        response.OutputStream.Write(responseBytes, 0, responseBytes.Length);

                        wasHandled = true;
                    }

                }

            }
            catch (Exception e)
            {
                Diagnostics.DumpException(e);
                wasHandled = false;
            }

            return wasHandled;
        }


        /// <summary>
        /// Parse HTTP request headers.
        /// Collect all HTTP HEADERS in the request, and return them as a map; with lower-case header keys, for consistency.
        /// NOTE: HTTP headers can sometimes repeat; but the .Net interface we access doesn't seem to support that; so we don't either.
        /// </summary>
        /// <param name="request"></param>
        /// <returns>dictionary with header-value mappings</returns>
        public static Dictionary<string, string> ParseHTTPRequestHeaders(HttpListenerRequest request)
        {
            Dictionary<string, string> headersMap = new Dictionary<string, string>();

            try
            {

                string[] allHeaders = request.Headers.AllKeys;
                foreach (string headerName in allHeaders)
                {
                    string headerValue = request.Headers.Get(headerName);

                    // Make the header name (key) LOWER CASE, for consistency (HTTP header names are case-insensitive) .
                    string headerNameKey = headerName.ToLower();

                    headersMap[headerNameKey] = headerValue;
                }
            }
            catch (Exception e)
            {
                Diagnostics.DumpException(e);
                headersMap = new Dictionary<string, string>();
            }

            return headersMap;
        }


        /// <summary>
        /// ReadHTTPRequestPOSTBody - read the contents (POST body) of an HTTP POST request, as a STRING (assumes non-binary string content).
        /// NOTE : You can only read the HTTP POST body ONCE per request. Thereafter request.InputStream is consumed, and not resettable.
        /// NOTE : This function ASSUMES ASCII data in the POST body; binary data will NOT be preserved in the stringization.
        /// </summary>
        /// <param name="request"></param>
        /// <returns>HTTP POST body as a string</returns>
        public static string ReadHTTPRequestPOSTBody(HttpListenerRequest request)
        {
            string postBody = "";

            try
            {
                if ((request.HttpMethod.ToUpper() == "POST") && request.HasEntityBody)
                {
                    using (System.IO.Stream body = request.InputStream)
                    {
                        //
                        // NOTE:  If the request times out, then the HttpListenerRequest object will be disposed of,
                        //        and the following may raise an exception, which we handle.
                        //
                        using (System.IO.StreamReader reader = new System.IO.StreamReader(body, request.ContentEncoding))
                        {
                            postBody = reader.ReadToEnd();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Diagnostics.DumpException(e);
                postBody = "";
            }

            return postBody;
        }


        /// <summary>
        /// ReadHTTPRequestPOSTBodyAsBytes - read the contents (POST body) of an HTTP POST request, as a BYTE ARRAY (for binary data, e.g. multipart HTTP image data)
        /// NOTE : You can only read the HTTP POST body ONCE per request. Thereafter request.InputStream is consumed, and not resettable.
        /// </summary>
        /// <param name="request"></param>
        /// <returns>HTTP POST body as a string</returns>
        public static byte[] ReadHTTPRequestPOSTBodyAsBytes(HttpListenerRequest request)
        {
            byte[] postBody = new byte[0];

            try
            {
                if ((request.HttpMethod.ToUpper() == "POST") && request.HasEntityBody)
                {
                    MemoryStream memStream = new MemoryStream();
                    request.InputStream.CopyTo(memStream);
                    postBody = memStream.ToArray();
                }
            }
            catch (Exception e)
            {
                Diagnostics.DumpException(e);
                postBody = new byte[0];
            }

            return postBody;
        }


        /// <summary>
        /// ParseRequestParamsFromUrl
        /// </summary>
        /// <param name="request"></param>
        /// <returns>dictionary with parameter-value mappings</returns>
        public static Dictionary<string, string> ParseRequestParamsFromUrl(HttpListenerRequest request)
        {
            Dictionary<string, string> paramsMap = new Dictionary<string, string>();

            try
            {
                string[] parts = request.RawUrl.Split('?');
                if (parts.Length == 2)
                {
                    string[] argKeyVals = parts[1].Split('&');
                    foreach (string keyVal in argKeyVals)
                    {
                        string[] keyValPair = keyVal.Split('=');

                        // Careful: there may be '=' characters as part of the value (e.g. Base64-encoded strings include '=' characters)
                        if (keyValPair.Length >= 2)
                        {
                            string key = keyValPair[0];
                            string value = String.Join("=", keyValPair.Skip(1));  // if there were '=' chars in the original string, concatenate then and re-insert the '=' chars that were removed by Split('=')
                            paramsMap[key] = value;
                        }
                    }
                }

                DebugPrint(2, " < SimpleUtils.WebServer.ParseRequestParamsFromUrl : params: {0} . ", JsonSerializer.Serialize((object)paramsMap));
            }
            catch (Exception e)
            {
                Diagnostics.DumpException(e);
            }

            return paramsMap;
        }


        /// <summary>
        /// ParseRequestParamsFromPOSTBody
        /// </summary>
        /// <param name="postBody"></param>
        /// <returns>dictionary with parameter-value mappings</returns>
        public static Dictionary<string, string> ParseRequestParamsFromPOSTBody(string postBody)
        {
            Dictionary<string, string> paramsMap = new Dictionary<string, string>();

            try
            {
                string[] argsList = postBody.Split('&');
                foreach (string keyVal in argsList)
                {
                    string[] keyValPair = keyVal.Split('=');

                    // Careful: there may be '=' characters as part of the value (e.g. Base64-encoded strings include '=' characters)
                    if (keyValPair.Length >= 2)
                    {
                        string key = keyValPair[0];
                        string value = String.Join("=", keyValPair.Skip(1));  // if there were '=' chars in the original string, concatenate then and re-insert the '=' chars that were removed by Split('=')
                        paramsMap[key] = value;
                    }
                }

                DebugPrint(2, " < SimpleUtils.WebServer.ParseRequestParamsFromPOSTBody : params: {0} . ", JsonSerializer.Serialize((object)paramsMap));
            }
            catch (Exception e)
            {
                Diagnostics.DumpException(e);
                paramsMap = new Dictionary<string, string>();
            }

            return paramsMap;
        }


        /// <summary>
        /// Indicate whether the response to an HTTP request is allowed to be GZIPPED (as indicated by the Accept-Encoding HTTP header in the request).
        /// </summary>
        /// <param name="request"></param>
        /// <returns>true iff response can be GZIPPED</returns>
        public static bool CanGZipHTTPResponse(HttpListenerRequest request)
        {
            //
            // Check the HTTP request headers to see if the web client ALLOWS the response to be GZIPPED.
            //
            string encodings = request.Headers.Get("Accept-Encoding");
            encodings = (encodings == null) ? "" : encodings.ToLower();
            bool canGzip = encodings.Contains("gzip");

            return canGzip;
        }


        /// <summary>
        /// TryToGZIPResponse - if client's HTTP request header allows, GZIP the response bytes (but only if it actually results in a smaller buffer).
        ///                     (if returning it, caller then needs to set the appropriate header : response.AppendHeader("Content-Encoding", "gzip"); )
        /// </summary>
        /// <param name="request"></param>
        /// <param name="responseBytes"></param>
        /// <returns>GZIPPED bytes; or null if not allowed, or if the GZIPPED bytes are larger then the unzipped response</returns>
        private static byte[] TryToGZIPResponse(HttpListenerRequest request, byte[] responseBytes)
        {
            byte[] gzippedBytes = null;

            if (CanGZipHTTPResponse(request))
            {
                gzippedBytes = DataUtils.DoGZIPCompression(responseBytes);

                if (gzippedBytes.Length < responseBytes.Length)
                {
                    DebugPrint(2, "TryToGZIPResponse: GZIPPED {0} bytes to {1} bytes ", responseBytes.Length, gzippedBytes.Length);
                }
                else
                {
                    // Sometimes the GZIP actually results in a LARGER buffer; e.g. for small buffers, or high-entropy data.  In this case, don't apply GZIP to the response.
                    DebugPrint(2, "TryToGZIPResponse: rejecting gzipping which did not result in smaller buffer ({0} bytes to {1} bytes) ", responseBytes.Length, gzippedBytes.Length);
                    gzippedBytes = null;
                }
            }

            return gzippedBytes;
        }


        //
        // GetLocalIPAddress - get the local IPv4 address
        //
        // If this returns non-null, localIPaddr.ToString() gives the stringized "0.0.0.0" form .
        //
        public static System.Net.IPAddress GetLocalIPAddress()
        {
            IPAddress localIPaddr = null;

            try
            {
                IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (IPAddress ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        localIPaddr = ip;
                    }
                }

            }
            catch (Exception e)
            {
                SimpleUtils.Diagnostics.DumpException(e);
            }

            return localIPaddr;
        }


        /// <summary>
        /// DebugPrint
        /// </summary>
        /// <param name="minVerboseLevel"></param>
        /// <param name="format"></param>
        /// <param name="varArgs"></param>
        private static void DebugPrint(int minVerboseLevel, string format, params dynamic[] varArgs)
        {
            if (VerboseLevel >= minVerboseLevel)
            {
                Console.WriteLine(format, varArgs);
            }
        }

    }
}
