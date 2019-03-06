﻿using System;
using System.IO;
using System.Text;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;
using System.Collections.Specialized;

namespace Yove.Http
{
    public class HttpClient
    {
        public NameValueCollection Headers = new NameValueCollection();
        public NameValueCollection Cookies { get; set; }

        public string Username { get; set; }
        public string Password { get; set; }
        public string Language { get; set; } = "en;q=0.9";

        public Encoding CharacterSet { get; set; }

        public bool KeepAlive { get; set; } = false;
        public bool EnableEncodingContent { get; set; } = true;
        public bool EnableAutoRedirect { get; set; } = true;
        public bool EnableProtocolError { get; set; } = true;
        public bool EnableCookies { get; set; } = true;
        public bool EnableReconnect { get; set; } = true;

        public int ReconnectLimit { get; set; } = 3;
        public int ReconnectDelay { get; set; } = 1000;
        public int TimeOut { get; set; } = 60000;
        public int ReadWriteTimeOut { get; set; } = 60000;

        public Uri Address { get; private set; }

        private HttpResponse Response { get; set; }

        private int ReconnectCount { get; set; }

        public string this[string Key]
        {
            get
            {
                if (string.IsNullOrEmpty(Key))
                    throw new ArgumentNullException("Key is null or empty.");

                return Headers[Key];
            }
            set
            {
                if (!string.IsNullOrEmpty(value))
                    Headers[Key] = value;
            }
        }

        public string UserAgent
        {
            get
            {
                return this["User-Agent"];
            }
            set
            {
                this["User-Agent"] = value;
            }
        }

        public string Authorization
        {
            get
            {
                return this["Authorization"];
            }
            set
            {
                this["Authorization"] = value;
            }
        }

        public string Referer
        {
            get
            {
                return this["Referer"];
            }
            set
            {
                this["Referer"] = value;
            }
        }

        private bool CanReconnect
        {
            get
            {
                return EnableReconnect && ReconnectCount < ReconnectLimit;
            }
        }

        internal TcpClient Connection { get; set; }
        internal NetworkStream NetworkStream { get; set; }
        internal Stream CommonStream { get; set; }
        internal HttpMethod Method { get; set; }
        internal HttpContent Content { get; set; }
        internal RemoteCertificateValidationCallback AcceptAllCertificationsCallback = new RemoteCertificateValidationCallback(AcceptAllCertifications);

        public void Dispose()
        {
            if (Connection != null)
            {
                Connection.Dispose();
                NetworkStream.Dispose();
                CommonStream.Dispose();
            }
        }

        public async Task<HttpResponse> Post(string URL)
        {
            return await Raw(HttpMethod.POST, URL);
        }

        public async Task<HttpResponse> Post(string URL, string Content, string ContentType = "application/json")
        {
            return await Raw(HttpMethod.POST, URL, new StringContent(Content)
            {
                ContentType = ContentType
            });
        }

        public async Task<HttpResponse> Post(string URL, byte[] Content, string ContentType = "application/octet-stream")
        {
            return await Raw(HttpMethod.POST, URL, new ByteContent(Content)
            {
                ContentType = ContentType
            });
        }

        public async Task<HttpResponse> Post(string URL, Stream Content, string ContentType = "application/octet-stream")
        {
            return await Raw(HttpMethod.POST, URL, new StreamContent(Content)
            {
                ContentType = ContentType
            });
        }

        public async Task<HttpResponse> Post(string URL, HttpContent Content)
        {
            return await Raw(HttpMethod.POST, URL, Content);
        }

        public async Task<HttpResponse> Get(string URL)
        {
            return await Raw(HttpMethod.GET, URL);
        }

        public async Task<string> GetString(string URL)
        {
            HttpResponse Response = await Raw(HttpMethod.GET, URL);

            return Response.Body;
        }

        public async Task<byte[]> GetBytes(string URL)
        {
            HttpResponse Response = await Raw(HttpMethod.GET, URL);

            return Response.ToBytes();
        }

        public async Task<MemoryStream> GetStream(string URL)
        {
            HttpResponse Response = await Raw(HttpMethod.GET, URL);

            return Response.ToMemoryStream();
        }

        public async Task<HttpResponse> Raw(HttpMethod Method, string URL, HttpContent Content = null)
        {
            Dispose();

            if (string.IsNullOrEmpty(URL))
                throw new ArgumentNullException("URL is null or empty.");

            this.Address = new UriBuilder(URL).Uri;
            this.Method = Method;
            this.Content = Content;

            if (EnableCookies)
                Cookies = new NameValueCollection();

            try
            {
                Connection = CreateConnection(Address.Host, Address.Port);
            }
            catch (Exception ex)
            {
                if (CanReconnect)
                    return await ReconnectFail();

                throw ex;
            }

            NetworkStream = Connection.GetStream();

            if (Address.Scheme.StartsWith("https"))
            {
                try
                {
                    SslStream SSL = new SslStream(NetworkStream, false, AcceptAllCertificationsCallback);
                    await SSL.AuthenticateAsClientAsync(Address.Host);

                    CommonStream = SSL;
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
            else
            {
                CommonStream = NetworkStream;
            }

            try
            {
                long ContentLength = 0L;
                string ContentType = null;

                if (Method != HttpMethod.GET && Content != null)
                {
                    ContentType = Content.ContentType;
                    ContentLength = Content.ContentLength;
                }

                byte[] StartingLineBytes = Encoding.ASCII.GetBytes($"{Method} {Address.PathAndQuery} HTTP/1.1\r\n");
                byte[] HeadersBytes = Encoding.ASCII.GetBytes(GenerateHeaders(Method, ContentLength, ContentType));

                await CommonStream.WriteAsync(StartingLineBytes, 0, StartingLineBytes.Length);
                await CommonStream.WriteAsync(HeadersBytes, 0, HeadersBytes.Length);

                if (Content != null && ContentLength != 0)
                    Content.Write(CommonStream);
            }
            catch
            {
                if (CanReconnect)
                    return await ReconnectFail();

                throw new Exception($"Failed send data to - {Address.AbsoluteUri}");
            }

            try
            {
                Response = new HttpResponse(this);
            }
            catch
            {
                if (CanReconnect)
                    return await ReconnectFail();

                throw new Exception($"Failed receive data from - {Address.AbsoluteUri}");
            }

            ReconnectCount = 0;

            if (EnableProtocolError)
            {
                if ((int)Response.StatusCode >= 400 && (int)Response.StatusCode < 500)
                    throw new Exception($"[Client] | Status Code - {Response.StatusCode}\r\n{Response.Body}");

                if ((int)Response.StatusCode >= 500)
                    throw new Exception($"[Server] | Status Code - {Response.StatusCode}\r\n{Response.Body}");
            }

            if (EnableAutoRedirect && Response.Location != null)
                return await Raw(Method, Response.Location, Content);

            return Response;
        }

        private TcpClient CreateConnection(string Host, int Port)
        {
            TcpClient TcpClient = new TcpClient();
            Exception ConnectionEx = null;

            ManualResetEventSlim ConnectionEvent = new ManualResetEventSlim();

            TcpClient.BeginConnect(Host, Port, new AsyncCallback((ar) =>
            {
                try
                {
                    TcpClient.EndConnect(ar);
                }
                catch (Exception ex)
                {
                    ConnectionEx = ex;
                }

                ConnectionEvent.Set();

            }), TcpClient);

            if (!ConnectionEvent.Wait(TimeOut) || ConnectionEx != null || !TcpClient.Connected)
            {
                TcpClient.Close();

                throw new Exception($"Failed Connection - {Address.AbsoluteUri}");
            }

            TcpClient.ReceiveTimeout = TcpClient.SendTimeout = ReadWriteTimeOut;

            return TcpClient;
        }

        private string GenerateHeaders(HttpMethod Method, long ContentLength = 0, string ContentType = null)
        {
            if (Address.IsDefaultPort)
                Headers["Host"] = Address.Host;
            else
                Headers["Host"] = $"{Address.Host}:{Address.Port}";

            if (KeepAlive)
                Headers["Connection"] = "keep-alive";
            else
                Headers["Connection"] = "close";

            if (!string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password))
            {
                string Auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));

                Headers["Authorization"] = $"Basic {Auth}";
            }

            if (EnableEncodingContent)
                Headers["Accept-Encoding"] = "gzip,deflate";

            Headers["Accept-Language"] = Language;

            if (CharacterSet != null)
            {
                if (CharacterSet != Encoding.UTF8)
                    Headers["Accept-Charset"] = $"{CharacterSet.WebName},utf-8";
                else
                    Headers["Accept-Charset"] = "utf-8";
            }

            if (Method != HttpMethod.GET)
            {
                if (ContentLength > 0)
                    Headers["Content-Type"] = ContentType;

                Headers["Content-Length"] = ContentLength.ToString();
            }

            StringBuilder Builder = new StringBuilder();

            foreach (var Header in Headers)
                Builder.AppendFormat($"{Header}: {Headers[(string)Header]}\r\n");

            Builder.AppendLine();

            return Builder.ToString();
        }

        private async Task<HttpResponse> ReconnectFail()
        {
            ReconnectCount++;
            await Task.Delay(ReconnectDelay);

            return await Raw(Method, Address.AbsoluteUri, Content);
        }

        private static bool AcceptAllCertifications(object sender, X509Certificate certification, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }
    }
}