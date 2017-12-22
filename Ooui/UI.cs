using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.WebSockets;

namespace Ooui
{
    public static class UI
    {
        static readonly ManualResetEvent started = new ManualResetEvent (false);

        [ThreadStatic]
        static System.Security.Cryptography.SHA256 sha256;

        static CancellationTokenSource serverCts;

        static readonly Dictionary<string, RequestHandler> publishedPaths =
            new Dictionary<string, RequestHandler> ();

        static readonly Dictionary<string, Style> styles =
            new Dictionary<string, Style> ();
        static readonly StyleSelectors rules = new StyleSelectors ();

        public static StyleSelectors Styles => rules;

        static readonly byte[] clientJsBytes;
        static readonly string clientJsEtag;

        public static byte[] ClientJsBytes => clientJsBytes;
        public static string ClientJsEtag => clientJsEtag;

        public static string Template { get; set; } = $@"<!DOCTYPE html>
<html>
<head>
  <title>@Title</title>
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
  <link rel=""stylesheet"" href=""https://ajax.aspnetcdn.com/ajax/bootstrap/3.3.7/css/bootstrap.min.css"" />
  <link rel=""stylesheet"" href=""https://gitcdn.github.io/bootstrap-toggle/2.2.2/css/bootstrap-toggle.min.css"" />
  <style>@Styles</style>
</head>
<body>
<div id=""ooui-body"" class=""container-fluid""></div>

<script type=""text/javascript"" src=""https://ajax.aspnetcdn.com/ajax/jquery/jquery-2.2.0.min.js""></script>
<script type=""text/javascript"" src=""https://gitcdn.github.io/bootstrap-toggle/2.2.2/js/bootstrap-toggle.min.js""></script>
<script src=""/ooui.js""></script>
<script>ooui(""@WebSocketPath"");</script>
</body>
</html>";

        static string host = "*";
        public static string Host {
            get => host;
            set {
                if (!string.IsNullOrWhiteSpace (value) && host != value) {
                    host = value;
                    Restart ();
                }
            }
        }
        static int port = 8080;
        public static int Port {
            get => port;
            set {
                if (port != value) {
                    port = value;
                    Restart ();
                }
            }
        }
        static bool serverEnabled = true;
        public static bool ServerEnabled {
            get => serverEnabled;
            set {
                if (serverEnabled != value) {
                    serverEnabled = value;
                    if (serverEnabled)
                        Restart ();
                    else
                        Stop ();
                }
            }
        }

        static UI ()
        {
            var asm = typeof(UI).Assembly;
            // System.Console.WriteLine("ASM = {0}", asm);
            // foreach (var n in asm.GetManifestResourceNames()) {
            //     System.Console.WriteLine("  {0}", n);
            // }
            using (var s = asm.GetManifestResourceStream ("Ooui.Client.js")) {
				if (s == null)
					throw new Exception ("Missing Client.js");
                using (var r = new StreamReader (s)) {
                    clientJsBytes = Encoding.UTF8.GetBytes (r.ReadToEnd ());
                }
            }
            clientJsEtag = "\"" + Hash (clientJsBytes) + "\"";
        }

        public static string Hash (byte[] bytes)
        {
            var sha = sha256;
            if (sha == null) {
                sha = System.Security.Cryptography.SHA256.Create ();
                sha256 = sha;
            }
            var data = sha.ComputeHash (bytes);
            StringBuilder sBuilder = new StringBuilder ();
            for (int i = 0; i < data.Length; i++) {
                sBuilder.Append (data[i].ToString ("x2"));
            }
            return sBuilder.ToString ();
        }

        static void Publish (string path, RequestHandler handler)
        {
            Console.WriteLine ($"PUBLISH {path} {handler}");
            lock (publishedPaths) publishedPaths[path] = handler;
            Start ();
        }

        public static void Publish (string path, Func<Element> elementCtor)
        {
            Publish (path, new ElementHandler (elementCtor));
        }

        public static void Publish (string path, Element element)
        {
            Publish (path, () => element);
        }

        public static void PublishFile (string filePath)
        {
            var path = "/" + System.IO.Path.GetFileName (filePath);
            PublishFile (path, filePath);
        }

        public static void PublishFile (string path, string filePath, string contentType = null)
        {
            var data = System.IO.File.ReadAllBytes (filePath);
            if (contentType == null) {
                contentType = GuessContentType (path, filePath);
            }
            var etag = "\"" + Hash (data) + "\"";
            Publish (path, new DataHandler (data, etag, contentType));
        }

        public static void PublishFile (string path, byte[] data, string contentType)
        {
            var etag = "\"" + Hash (data) + "\"";
            Publish (path, new DataHandler (data, etag, contentType));
        }

        public static void PublishFile (string path, byte[] data, string etag, string contentType)
        {            
            Publish (path, new DataHandler (data, etag, contentType));
        }

        public static bool TryGetFileContentAtPath (string path, out FileContent file)
        {
            RequestHandler handler;
            lock (publishedPaths) {
                if (!publishedPaths.TryGetValue (path, out handler)) {
                    file = null;
                    return false;
                }
            }
            if (handler is DataHandler dh) {
                file = new FileContent {
                    Etag = dh.Etag,
                    Content = dh.Data,
                    ContentType = dh.ContentType,
                };
                return true;
            }
            file = null;
            return false;
        }

        public class FileContent
        {
            public string ContentType { get; set; }
            public string Etag { get; set; }
            public byte[] Content { get; set; }
        }

        public static void PublishJson (string path, Func<object> ctor)
        {
            Publish (path, new JsonHandler (ctor));
        }

        public static void PublishJson (string path, object value)
        {
            var data = JsonHandler.GetData (value);
            var etag = "\"" + Hash (data) + "\"";
            Publish (path, new DataHandler (data, etag, JsonHandler.ContentType));
        }

		public static void PublishCustomResponse (string path, Action<HttpListenerContext, CancellationToken> responder)
		{
			Publish (path, new CustomHandler (responder));
		}

        static string GuessContentType (string path, string filePath)
        {
            return null;
        }

        public static void Present (string path, object presenter = null)
        {
            WaitUntilStarted ();
            var url = GetUrl (path);
            Console.WriteLine ($"PRESENT {url}");
			Platform.OpenBrowser (url, presenter);
		}

        public static string GetUrl (string path)
        {
            var localhost = host == "*" ? "localhost" : host;
            var url = $"http://{localhost}:{port}{path}";
            return url;
        }

        public static void WaitUntilStarted () => started.WaitOne ();

        static void Start ()
        {
            if (!serverEnabled) return;
            if (serverCts != null) return;
            serverCts = new CancellationTokenSource ();
            var token = serverCts.Token;
            var listenerPrefix = $"http://{host}:{port}/";
            Task.Run (() => RunAsync (listenerPrefix, token), token);
        }

        static void Stop ()
        {
            var scts = serverCts;
            if (scts == null) return;
            serverCts = null;
            started.Reset ();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine ($"Stopping...");
            Console.ResetColor ();

            scts.Cancel ();
        }

        static void Restart ()
        {
            if (serverCts == null) return;
            Stop ();
            Start ();
        }

        static async Task RunAsync (string listenerPrefix, CancellationToken token)
        {
            HttpListener listener = null;
            var wait = 5;

            started.Reset ();
            while (!started.WaitOne(0) && !token.IsCancellationRequested) {
                try {
                    listener = new HttpListener ();
                    listener.Prefixes.Add (listenerPrefix);
                    listener.Start ();
                    started.Set ();
                }
                catch (System.Net.Sockets.SocketException ex) {
                    Console.WriteLine ($"{listenerPrefix} error: {ex.Message}. Trying again in {wait} seconds...");
                    await Task.Delay (wait * 1000).ConfigureAwait (false);
                }
                catch (System.Net.HttpListenerException ex) {
                    Console.WriteLine ($"{listenerPrefix} error: {ex.Message}. Trying again in {wait} seconds...");
                    await Task.Delay (wait * 1000).ConfigureAwait (false);
                }
                catch (Exception ex) {
                    Error ("Error listening", ex);
                    return;
                }
            }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine ($"Listening at {listenerPrefix}...");
            Console.ResetColor ();

            while (!token.IsCancellationRequested) {
                var listenerContext = await listener.GetContextAsync ().ConfigureAwait (false);
                if (listenerContext.Request.IsWebSocketRequest) {
                    ProcessWebSocketRequest (listenerContext, token);
                }
                else {
                    ProcessRequest (listenerContext, token);
                }
            }
        }

        static void ProcessRequest (HttpListenerContext listenerContext, CancellationToken token)
        {
            var url = listenerContext.Request.Url;
            var path = url.LocalPath;

            Console.WriteLine ($"{listenerContext.Request.HttpMethod} {url.LocalPath}");

            var response = listenerContext.Response;

            if (path == "/ooui.js") {
                var inm = listenerContext.Request.Headers.Get ("If-None-Match");
                if (string.IsNullOrEmpty (inm) || inm != clientJsEtag) {
                    response.StatusCode = 200;
                    response.ContentLength64 = clientJsBytes.LongLength;
                    response.ContentType = "application/javascript";
                    response.ContentEncoding = Encoding.UTF8;
                    response.AddHeader ("Cache-Control", "public, max-age=60");
                    response.AddHeader ("Etag", clientJsEtag);
                    using (var s = response.OutputStream) {
                        s.Write (clientJsBytes, 0, clientJsBytes.Length);
                    }
                    response.Close ();
                }
                else {
                    response.StatusCode = 304;
                    response.Close ();
                }
            }
            else {
                var found = false;
                RequestHandler handler;
                lock (publishedPaths) found = publishedPaths.TryGetValue (path, out handler);
                if (found) {
                    try {
                        handler.Respond (listenerContext, token);
                    }
                    catch (Exception ex) {
                        Error ("Handler failed to respond", ex);
                        try {
                            response.StatusCode = 500;
                            response.Close ();
                        }
                        catch {
                            // Ignore ending the response errors
                        }
                    }
                }
                else {
                    response.StatusCode = 404;
                    response.Close ();
                }
            }
        }

        abstract class RequestHandler
        {
            public abstract void Respond (HttpListenerContext listenerContext, CancellationToken token);
        }

        class ElementHandler : RequestHandler
        {
            readonly Lazy<Element> element;

            public ElementHandler (Func<Element> ctor)
            {
                element = new Lazy<Element> (ctor);
            }

            public Element GetElement () => element.Value;

            public override void Respond (HttpListenerContext listenerContext, CancellationToken token)
            {
                var url = listenerContext.Request.Url;
                var path = url.LocalPath;
                var response = listenerContext.Response;

                response.StatusCode = 200;
                response.ContentType = "text/html";
                response.ContentEncoding = Encoding.UTF8;
                var html = Encoding.UTF8.GetBytes (RenderTemplate (path));
                response.ContentLength64 = html.LongLength;
                using (var s = response.OutputStream) {
                    s.Write (html, 0, html.Length);
                }
                response.Close ();
            }
        }

        public static string RenderTemplate (string webSocketPath, string title = "")
        {
            return Template.Replace ("@WebSocketPath", webSocketPath).Replace ("@Styles", rules.ToString ()).Replace ("@Title", title);
        }

        class DataHandler : RequestHandler
        {
            readonly byte[] data;
            readonly string etag;
            readonly string contentType;

            public byte[] Data => data;
            public string Etag => etag;
            public string ContentType => contentType;

            public DataHandler (byte[] data, string etag, string contentType = null)
            {
                this.data = data;
                this.etag = etag;
                this.contentType = contentType;
            }

            public override void Respond (HttpListenerContext listenerContext, CancellationToken token)
            {
                var url = listenerContext.Request.Url;
                var path = url.LocalPath;
                var response = listenerContext.Response;

                var inm = listenerContext.Request.Headers.Get ("If-None-Match");
                if (!string.IsNullOrEmpty (inm) && inm == etag) {
                    response.StatusCode = 304;
                }
                else {
                    response.StatusCode = 200;
                    response.AddHeader ("Etag", etag);
                    if (!string.IsNullOrEmpty (contentType))
                        response.ContentType = contentType;
                    response.ContentLength64 = data.LongLength;

                    using (var s = response.OutputStream) {
                        s.Write (data, 0, data.Length);
                    }
                }
                response.Close ();
            }
        }

        class JsonHandler : RequestHandler
        {
            public const string ContentType = "application/json; charset=utf-8";

            readonly Func<object> ctor;

            public JsonHandler (Func<object> ctor)
            {
                this.ctor = ctor;
            }

            public static byte[] GetData (object obj)
            {
                var r = Newtonsoft.Json.JsonConvert.SerializeObject (obj);
                return System.Text.Encoding.UTF8.GetBytes (r);
            }

            public override void Respond (HttpListenerContext listenerContext, CancellationToken token)
            {
                var response = listenerContext.Response;

                var data = GetData (ctor ());

                response.StatusCode = 200;
                response.ContentType = ContentType;
                response.ContentLength64 = data.LongLength;

                using (var s = response.OutputStream) {
                    s.Write (data, 0, data.Length);
                }
                response.Close ();
            }
        }

		class CustomHandler : RequestHandler
		{
			readonly Action<HttpListenerContext, CancellationToken> responder;

			public CustomHandler (Action<HttpListenerContext, CancellationToken> responder)
			{
				this.responder = responder;
			}

			public override void Respond (HttpListenerContext listenerContext, CancellationToken token)
			{
				responder (listenerContext, token);
			}
		}

        static async void ProcessWebSocketRequest (HttpListenerContext listenerContext, CancellationToken serverToken)
        {
            //
            // Find the element
            //
            var url = listenerContext.Request.Url;
            var path = url.LocalPath;

            RequestHandler handler;
            var found = false;
            lock (publishedPaths) found = publishedPaths.TryGetValue (path, out handler);
            var elementHandler = handler as ElementHandler;
            if (!found || elementHandler == null) {
                listenerContext.Response.StatusCode = 404;
                listenerContext.Response.Close ();
                return;
            }

            Element element = null;
            try {
                element = elementHandler.GetElement ();

				if (element == null)
					throw new Exception ("Handler returned a null element");
            }
            catch (Exception ex) {
                listenerContext.Response.StatusCode = 500;
                listenerContext.Response.Close();
                Error ("Failed to create element", ex);
                return;
            }

            //
            // Connect the web socket
            //
            WebSocketContext webSocketContext = null;
            WebSocket webSocket = null;
            try {
                webSocketContext = await listenerContext.AcceptWebSocketAsync (subProtocol: "ooui").ConfigureAwait (false);
                webSocket = webSocketContext.WebSocket;
                Console.WriteLine ("WEBSOCKET {0}", listenerContext.Request.Url.LocalPath);
            }
            catch (Exception ex) {
                listenerContext.Response.StatusCode = 500;
                listenerContext.Response.Close();
                Error ("Failed to accept WebSocket", ex);
                return;
            }

            //
            // Set the element's dimensions
            //
            var query =
                (from part in listenerContext.Request.Url.Query.Split (new[] { '?', '&' })
                 where part.Length > 0
                 let kvs = part.Split ('=')
                 where kvs.Length == 2
                 select kvs).ToDictionary (x => Uri.UnescapeDataString (x[0]), x => Uri.UnescapeDataString (x[1]));
            if (!query.TryGetValue ("w", out var wValue) || string.IsNullOrEmpty (wValue)) {
                wValue = "640";
            }
            if (!query.TryGetValue ("h", out var hValue) || string.IsNullOrEmpty (hValue)) {
                hValue = "480";
            }
            var icult = System.Globalization.CultureInfo.InvariantCulture;
            if (!double.TryParse (wValue, System.Globalization.NumberStyles.Any, icult, out var w))
                w = 640;
            if (!double.TryParse (hValue, System.Globalization.NumberStyles.Any, icult, out var h))
                h = 480;

            //
            // Create a new session and let it handle everything from here
            //
            try {
                var session = new Session (webSocket, element, w, h, serverToken);
                await session.RunAsync ().ConfigureAwait (false);
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely) {
                // The remote party closed the WebSocket connection without completing the close handshake.
            }
            catch (Exception ex) {
                Error ("Web socket failed", ex);
            }
            finally {
                webSocket?.Dispose ();
            }
        }

        static void Error (string message, Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine ("{0}: {1}", message, ex);
            Console.ResetColor ();
        }

        public class Session
        {
            readonly WebSocket webSocket;
            readonly Element element;
            readonly Action<Message> handleElementMessageSent;

            readonly CancellationTokenSource sessionCts = new CancellationTokenSource ();
            readonly CancellationTokenSource linkedCts;
            readonly CancellationToken token;

            readonly HashSet<string> createdIds;
            readonly List<Message> queuedMessages = new List<Message> ();

            public const int MaxFps = 30;

            readonly System.Timers.Timer sendThrottle;
            DateTime lastTransmitTime = DateTime.MinValue;
            readonly TimeSpan throttleInterval = TimeSpan.FromSeconds (1.0 / MaxFps);
            readonly double initialWidth;
            readonly double initialHeight;

            public Session (WebSocket webSocket, Element element, double initialWidth, double initialHeight, CancellationToken serverToken)
            {
                this.webSocket = webSocket;
                this.element = element;
                this.initialWidth = initialWidth;
                this.initialHeight = initialHeight;

                //
                // Create a new session cancellation token that will trigger
                // automatically if the server shutsdown or the session shutsdown.
                //
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource (serverToken, sessionCts.Token);
                token = linkedCts.Token;

                //
                // Keep a list of all the elements for which we've transmitted the initial state
                //
                createdIds = new HashSet<string> {
                    "window",
                    "document",
                    "document.body",
                };

                //
                // Preparse handlers for the element
                //
                handleElementMessageSent = QueueMessage;

                //
                // Create a timer to use as a throttle when sending messages
                //
                sendThrottle = new System.Timers.Timer (throttleInterval.TotalMilliseconds);
                sendThrottle.Elapsed += (s, e) => {
                    // System.Console.WriteLine ("TICK SEND THROTTLE FOR {0}", element);
                    if ((e.SignalTime - lastTransmitTime) >= throttleInterval) {
                        sendThrottle.Enabled = false;
                        lastTransmitTime = e.SignalTime;
                        TransmitQueuedMessages ();
                    }
                };
            }

            public async Task RunAsync ()
            {
                //
                // Start watching for changes in the element
                //
                element.MessageSent += handleElementMessageSent;

                try {
                    //
                    // Add it to the document body
                    //
                    if (element.WantsFullScreen) {
                        element.Style.Width = initialWidth;
                        element.Style.Height = initialHeight;
                    }
                    QueueMessage (Message.Call ("document.body", "appendChild", element));

                    //
                    // Start the Read Loop
                    //
                    var receiveBuffer = new byte[64*1024];

                    while (webSocket.State == WebSocketState.Open && !token.IsCancellationRequested) {
                        var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), token).ConfigureAwait (false);

                        if (receiveResult.MessageType == WebSocketMessageType.Close) {
                            await webSocket.CloseAsync (WebSocketCloseStatus.NormalClosure, "", token).ConfigureAwait (false);
                            sessionCts.Cancel ();
                        }
                        else if (receiveResult.MessageType == WebSocketMessageType.Binary) {
                            await webSocket.CloseAsync (WebSocketCloseStatus.InvalidMessageType, "Cannot accept binary frame", token).ConfigureAwait (false);
                            sessionCts.Cancel ();
                        }
                        else {
                            var size = receiveResult.Count;
                            while (!receiveResult.EndOfMessage) {
                                if (size >= receiveBuffer.Length) {
                                    await webSocket.CloseAsync (WebSocketCloseStatus.MessageTooBig, "Message too big", token).ConfigureAwait (false);
                                    return;
                                }
                                receiveResult = await webSocket.ReceiveAsync (new ArraySegment<byte>(receiveBuffer, size, receiveBuffer.Length - size), token).ConfigureAwait (false);
                                size += receiveResult.Count;
                            }
                            var receivedString = Encoding.UTF8.GetString (receiveBuffer, 0, size);

                            try {
                                // Console.WriteLine ("RECEIVED: {0}", receivedString);
                                var message = Newtonsoft.Json.JsonConvert.DeserializeObject<Message> (receivedString);
                                element.Receive (message);
                            }
                            catch (Exception ex) {
                                Error ("Failed to process received message", ex);
                            }
                        }
                    }
                }
                finally {
                    element.MessageSent -= handleElementMessageSent;
                }
            }

            void QueueStateMessagesLocked (EventTarget target)
            {
                if (target == null) return;
                var created = false;
                foreach (var m in target.StateMessages) {
                    if (m.MessageType == MessageType.Create) {
                        createdIds.Add (m.TargetId);
                        created = true;
                    }
                    if (created) {
                        QueueMessageLocked (m);
                    }
                }
            }

            void QueueMessageLocked (Message message)
            {
                //
                // Make sure all the referenced objects have been created
                //
                if (!createdIds.Contains (message.TargetId)) {
                    QueueStateMessagesLocked (element.GetElementById (message.TargetId));
                }
                if (message.Value is EventTarget ve) {
                    if (!createdIds.Contains (ve.Id)) {
                        QueueStateMessagesLocked (ve);
                    }
                }
                else if (message.Value is Array a) {
                    for (var i = 0; i < a.Length; i++) {
                        // Console.WriteLine ($"A{i} = {a.GetValue(i)}");
                        if (a.GetValue (i) is EventTarget e && !createdIds.Contains (e.Id)) {
                            QueueStateMessagesLocked (e);
                        }
                    }
                }

                //
                // Add it to the queue
                //
                //Console.WriteLine ($"QM {message.MessageType} {message.TargetId} {message.Key} {message.Value}");
                queuedMessages.Add (message);
            }

            void QueueMessage (Message message)
            {
                lock (queuedMessages) {
                    QueueMessageLocked (message);
                }
                sendThrottle.Enabled = true;
            }

            async void TransmitQueuedMessages ()
            {
                try {
                    //
                    // Dequeue as many messages as we can
                    //
                    var messagesToSend = new List<Message> ();
                    System.Runtime.CompilerServices.ConfiguredTaskAwaitable task;
                    lock (queuedMessages) {
                        messagesToSend.AddRange (queuedMessages);
                        queuedMessages.Clear ();

                        if (messagesToSend.Count == 0)
                            return;

                        //
                        // Now actually send this message
                        // Do this while locked to make sure SendAsync is called in the right order
                        //
                        var json = Newtonsoft.Json.JsonConvert.SerializeObject (messagesToSend);
                        var outputBuffer = new ArraySegment<byte> (Encoding.UTF8.GetBytes (json));
                        //Console.WriteLine ("TRANSMIT " + json);
                        task = webSocket.SendAsync (outputBuffer, WebSocketMessageType.Text, true, token).ConfigureAwait (false);
                    }
                    await task;
                }
                catch (Exception ex) {                        
                    Error ("Failed to send queued messages, aborting session", ex);
                    element.MessageSent -= handleElementMessageSent;
                    sessionCts.Cancel ();
                }
            }
        }

        public class StyleSelectors
        {
            public Style this[string selector] {
                get {
                    var key = selector ?? "";
                    lock (styles) {
                        if (!styles.TryGetValue (key, out Style r)) {
                            r = new Style ();
                            styles.Add (key, r);
                        }
                        return r;
                    }
                }
                set {
                    var key = selector ?? "";
                    lock (styles) {
                        if (value == null) {
                            styles.Remove (key);
                        }
                        else {
                            styles[key] = value;
                        }
                    }
                }
            }

            public void Clear ()
            {
                lock (styles) {
                    styles.Clear ();
                }
            }

            public override string ToString()
            {
                lock (styles) {
                    var q =
                        from s in styles
                        let v = s.Value.ToString ()
                        where v.Length > 0
                        select s.Key + " {" + s.Value.ToString () + "}";
                    return String.Join ("\n", q);
                }
            }
        }
    }
}
