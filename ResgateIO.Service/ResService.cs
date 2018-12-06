﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using NATS.Client;
using Newtonsoft.Json;

namespace ResgateIO.Service
{
    public class ResService
    {
        private enum State { Stopped, Starting, Started, Stopping };

        private State state = State.Stopped;
        private readonly Object stateLock = new Object();

        public IConnection Connection { get; private set; }
        private Dictionary<string, IAsyncSubscription> subs;
        private Dictionary<string, Work> rwork;
        private readonly PatternTree patterns = new PatternTree();

        public String Name { get; }

        // Predefined raw data responses
        internal static readonly byte[] ResponseAccessDenied = Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"system.accessDenied\",\"message\":\"Access denied\"}}");
        internal static readonly byte[] ResponseInternalError = Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"system.internalError\",\"message\":\"Internal error\"}}");
        internal static readonly byte[] ResponseNotFound = Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"system.notFound\",\"message\":\"Not found\"}}");
        internal static readonly byte[] ResponseMethodNotFound = Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"system.methodNotFound\",\"message\":\"Method not found\"}}");
        internal static readonly byte[] ResponseInvalidParams = Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"system.invalidParams\",\"message\":\"Invalid parameters\"}}");
        internal static readonly byte[] ResponseMissingResponse = Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"system.internalError\",\"message\":\"Internal error: missing response\"}}");
        internal static readonly byte[] ResponseAccessGranted = Encoding.UTF8.GetBytes("{\"result\":{\"get\":true,\"call\":\"*\"}}");
        internal static readonly byte[] ResponseSuccess = Encoding.UTF8.GetBytes("{\"result\":null}");

        /// <summary>
        /// Creates a new ResService
        /// </summary>
        /// <param name="name">Name of the service. The name must be a non-empty alphanumeric string with no embedded whitespace.</param>
        public ResService(string name)
        {
            Name = name;
        }

        /// <summary>
        /// Registers a handler for the given resource pattern.
        ///
        /// A pattern may contain placeholders that acts as wildcards, and will be
        /// parsed and stored in the request.PathParams map.
        /// A placeholder is a resource name part starting with a dollar ($) character:
        ///   s.MapHandler("user.$id", handler) // Will match "user.10", "user.foo", etc.
        ///
        /// If the pattern is already registered, or if there are conflicts among
        /// the handlers, an exception will be thrown.
        /// </summary>
        /// <remarks>The handler must not implement the ICollectionHandler.</remarks>
        /// <param name="pattern">Resource pattern</param>
        /// <param name="handler">Resource handler</param>
        public void MapHandler(String pattern, IResourceHandler handler)
        {
            if (handler is ICollectionHandler && handler is IModelHandler)
            {
                throw new ArgumentException("Handler must not implement both IModelHandler and ICollectionHandler");
            }
            patterns.Add(Name + "." + pattern, handler);
        }

        /// <summary>
        /// Subscribes to incoming requests on the IConnection, serving them on
        /// a single thread in the order they are received. For each request,
        /// it calls the appropriate handler method.
        /// </summary>
        /// <param name="conn">Connection to NATS Server</param>
        public void Serve(IConnection conn) {
            lock (stateLock)
            {
                if (state != State.Stopped)
                {
                    throw new Exception("Service is not stopped.");
                }
                state = State.Starting;
            }

            serve(conn);
        }

        /// <summary>
        /// Connects to the NATS server at the url. Once connected,
        /// it subscribes to incoming requests and serves them on a single thread
        /// in the order they are received. For each request, it calls the appropriate
        /// handler method.
        ///
        /// In case of disconnect, it will try to reconnect until Close is called,
        /// or until successfully reconnecting, upon which Reset will be called.
        /// </summary>
        /// <param name="url">URL to NATS Server.</param>
        public void Serve(string url) {
            lock (stateLock)
            {
                if (state != State.Stopped)
                {
                    throw new Exception("Service is not stopped.");
                }
                state = State.Starting;
            }

            ConnectionFactory cf = new ConnectionFactory();
            IConnection conn = cf.CreateConnection(url);
            serve(conn);
        }

        private void serve(IConnection conn)
        {
            Connection = conn;
            rwork = new Dictionary<string, Work>();

            lock (stateLock)
            {
                state = State.Started;
            }

            subscribe();
        }

        private void subscribe()
        {
            var types = Enum.GetValues(typeof(RequestType));
            subs = new Dictionary<string, IAsyncSubscription>(types.Length);

            foreach (RequestType type in types)
            {
                String typeStr = type.ToActionString();
                IAsyncSubscription sub = Connection.SubscribeAsync(typeStr + "." + Name + ".>", handleMessage);
                subs[typeStr] = sub;
            }
        }

        private void handleMessage(object sender, MsgHandlerEventArgs e)
        {
            Msg msg = e.Message;
            String subj = msg.Subject;

            Console.WriteLine("==> {0}: {1}", subj, Encoding.UTF8.GetString(msg.Data));

            // Assert there is a reply subject
            if (String.IsNullOrEmpty(msg.Reply))
            {
                Console.WriteLine("Missing reply subject on request: {0}", subj);
                return;
            }

            // Get request type
            Int32 idx = subj.IndexOf('.');
            if (idx < 0) {
                // Shouldn't be possible unless NATS is really acting up
                Console.WriteLine("Invalid request subject: {0}", subj);
                return;
            }
            String method = null;
            String rtype = subj.Substring(0, idx);
            String rname = subj.Substring(idx + 1);

            if (rtype == "call" || rtype == "auth")
            {
                Int32 lastIdx = rname.LastIndexOf('.');
                if (idx < 0)
                {
                    // No method? Resgate must be acting up
                    Console.WriteLine("Invalid request subject: {0}", subj);
                    return;
                }
                method = rname.Substring(lastIdx + 1);
                rname = rname.Substring(0, lastIdx);
            }

            RunWith(rname, () => processRequest(msg, rtype, rname, method));
        }

        public void RunWith(String resourceName, Action callback)
        {
            lock (stateLock)
            {
                if (state != State.Started)
                {
                    return;
                }


                if (rwork.TryGetValue(resourceName, out Work work))
                {
                    work.AddTask(callback);
                }
                else
                {
                    work = new Work(resourceName, callback);
                    rwork.Add(resourceName, work);
                    ThreadPool.QueueUserWorkItem(new WaitCallback(processWork), work);
                }
            }
        }

        /// <summary>
        /// Sends a connection token event that sents the connection's access token,
        /// discarding any previously set token.
        /// A change of token will invalidate any previous access response received using the old token.
        /// </summary>
        /// <remarks>
        /// See the protocol specification for more information:
        ///    https://github.com/jirenius/resgate/blob/master/docs/res-service-protocol.md#connection-token-event
        /// </remarks>
        /// <param name="cid">Connection ID</param>
        /// <param name="token">Access token. A null token clears any previously set token</param>
        public void ConnectionTokenEvent(string cid, object token)
        {
            Send("conn." + cid + ".token", token);
        }

        /// <summary>
        /// Sends a system.reset event to trigger any gateway to invalidate their cache for this service
        /// and request the resource anew.
        /// </summary>
        /// <remarks>
        /// See the protocol specification for more information:
        ///    https://github.com/jirenius/resgate/blob/master/docs/res-service-protocol.md#system-reset-event
        /// </remarks>
        public void Reset()
        {
            // TODO: Reset should be based on actual registered patterns
            // instead of wildcarded on the service name.
            Send("system.reset", new SystemResetDto(Name + ".>", Name + ".>"));
        }

        /// <summary>
        /// Sends a raw data message to NATS server on a given subject,
        /// logging any exception.
        /// </summary>
        /// <param name="subject">Message subject</param>
        /// <param name="data">Message JSON encoded data</param>
        internal void RawSend(string subject, byte[] data)
        {
            try
            {
                Connection.Publish(subject, data);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error sending message {0}: {1}", subject, ex.Message);
            }
        }


        /// <summary>
        /// Sends a message to NATS server on a given subject,
        /// logging any exception.
        /// </summary>
        /// <param name="subject">Message subject</param>
        /// <param name="payload">Message payload</param>
        internal void Send(string subject, object payload)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
                RawSend(subject, data);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error serializing event payload for {0}: {1}", subject, ex.Message);
            }
        }

        private void processWork(Object obj)
        {
            Work work = (Work)obj;
            Action task;

            while (true) {
                lock (stateLock)
                {
                    task = work.NextTask();
                    // Check if work tasks are exhausted
                    if (task == null)
                    {
                        // Work completed
                        rwork.Remove(work.ResourceName);
                        return;
                    }
                }

                task();
            }
        }
        
        private void processRequest(Msg msg, String rtype, String rname, String method)
        {
            PatternTree.Match match = patterns.Get(rname);
            Request req;

            // Check if there is no matching handler
            if (match == null)
            {
                throw new NotImplementedException();
            }

            try
            {
                RequestDto reqInput = deserialize<RequestDto>(msg.Data);
                req = new Request(
                    this,
                    msg,
                    rtype,
                    rname,
                    method,
                    match.Handler,
                    match.Params,
                    reqInput.CID,
                    reqInput.RawParams,
                    reqInput.RawToken,
                    reqInput.Header,
                    reqInput.Host,
                    reqInput.RemoteAddr,
                    reqInput.URI,
                    reqInput.Query);
            }
            catch(Exception ex)
            {
                Console.WriteLine("Error deserializing incoming request: %s", Encoding.UTF8.GetString(msg.Data));
                req = new Request(this, msg, rtype, rname, method, match.Handler, match.Params);
                req.Error(new ResError(ex));
                return;
            }

            req.ExecuteHandler();
        }

        private static T deserialize<T>(byte[] data) where T : class
        {
            using (var stream = new MemoryStream(data))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return JsonSerializer.Create().Deserialize(reader, typeof(T)) as T;
        }
    }
}
