// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.Test.Common;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.XUnitExtensions;
using Microsoft.DotNet.RemoteExecutor;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.Http.Functional.Tests
{
    using Configuration = System.Net.Test.Common.Configuration;

#if WINHTTPHANDLER_TEST
    using HttpClientHandler = System.Net.Http.WinHttpClientHandler;
#endif

    // Note:  Disposing the HttpClient object automatically disposes the handler within. So, it is not necessary
    // to separately Dispose (or have a 'using' statement) for the handler.
    public abstract class HttpClientHandlerTest : HttpClientHandlerTestBase
    {
        private const string ExpectedContent = "Test content";
        private const string Username = "testuser";
        private const string Password = "password";
        private const string HttpDefaultPort = "80";

        private readonly NetworkCredential _credential = new NetworkCredential(Username, Password);

        public static readonly object[][] Http2Servers = Configuration.Http.Http2Servers;
        public static readonly object[][] Http2NoPushServers = Configuration.Http.Http2NoPushServers;

        // Standard HTTP methods defined in RFC7231: http://tools.ietf.org/html/rfc7231#section-4.3
        //     "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "TRACE"
        public static readonly IEnumerable<object[]> HttpMethods =
            GetMethods("GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "TRACE", "CUSTOM1");
        public static readonly IEnumerable<object[]> HttpMethodsThatAllowContent =
            GetMethods("GET", "POST", "PUT", "DELETE", "OPTIONS", "CUSTOM1");
        public static readonly IEnumerable<object[]> HttpMethodsThatDontAllowContent =
            GetMethods("HEAD", "TRACE");

        private static bool IsWindows10Version1607OrGreater => PlatformDetection.IsWindows10Version1607OrGreater;

        private static IEnumerable<object[]> GetMethods(params string[] methods)
        {
            foreach (string method in methods)
            {
                foreach (Uri serverUri in Configuration.Http.EchoServerList)
                {
                    yield return new object[] { method, serverUri };
                }
            }
        }

        public HttpClientHandlerTest(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void CookieContainer_SetNull_ThrowsArgumentNullException()
        {
            using (HttpClientHandler handler = CreateHttpClientHandler())
            {
                Assert.Throws<ArgumentNullException>(() => handler.CookieContainer = null);
            }
        }

        [Fact]
        public void Ctor_ExpectedDefaultPropertyValues_CommonPlatform()
        {
            using (HttpClientHandler handler = CreateHttpClientHandler())
            {
                Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
                Assert.True(handler.AllowAutoRedirect);
                Assert.Equal(ClientCertificateOption.Manual, handler.ClientCertificateOptions);
                CookieContainer cookies = handler.CookieContainer;
                Assert.NotNull(cookies);
                Assert.Equal(0, cookies.Count);
                Assert.Null(handler.Credentials);
                Assert.Equal(50, handler.MaxAutomaticRedirections);
                Assert.NotNull(handler.Properties);
                Assert.Null(handler.Proxy);
                Assert.True(handler.SupportsAutomaticDecompression);
                Assert.True(handler.UseCookies);
                Assert.False(handler.UseDefaultCredentials);
                Assert.True(handler.UseProxy);
            }
        }

        [Fact]
        public void Ctor_ExpectedDefaultPropertyValues()
        {
            using (HttpClientHandler handler = CreateHttpClientHandler())
            {
                Assert.Equal(64, handler.MaxResponseHeadersLength);
                Assert.False(handler.PreAuthenticate);
                Assert.True(handler.SupportsProxy);
                Assert.True(handler.SupportsRedirectConfiguration);

                // Changes from .NET Framework.
                Assert.False(handler.CheckCertificateRevocationList);
                Assert.Equal(0, handler.MaxRequestContentBufferSize);
                Assert.Equal(SslProtocols.None, handler.SslProtocols);
            }
        }

        [Fact]
        public void Credentials_SetGet_Roundtrips()
        {
            using (HttpClientHandler handler = CreateHttpClientHandler())
            {
                var creds = new NetworkCredential("username", "password", "domain");

                handler.Credentials = null;
                Assert.Null(handler.Credentials);

                handler.Credentials = creds;
                Assert.Same(creds, handler.Credentials);

                handler.Credentials = CredentialCache.DefaultCredentials;
                Assert.Same(CredentialCache.DefaultCredentials, handler.Credentials);
            }
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        public void MaxAutomaticRedirections_InvalidValue_Throws(int redirects)
        {
            using (HttpClientHandler handler = CreateHttpClientHandler())
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => handler.MaxAutomaticRedirections = redirects);
            }
        }

#if !WINHTTPHANDLER_TEST
        [Theory]
        [InlineData(-1)]
        [InlineData((long)int.MaxValue + (long)1)]
        public void MaxRequestContentBufferSize_SetInvalidValue_ThrowsArgumentOutOfRangeException(long value)
        {
            using (HttpClientHandler handler = CreateHttpClientHandler())
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => handler.MaxRequestContentBufferSize = value);
            }
        }
#endif

        [OuterLoop("Uses external servers")]
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        [OuterLoop("Uses external servers")]
        public async Task UseDefaultCredentials_SetToFalseAndServerNeedsAuth_StatusCodeUnauthorized(bool useProxy)
        {
            HttpClientHandler handler = CreateHttpClientHandler();
            handler.UseProxy = useProxy;
            handler.UseDefaultCredentials = false;
            using (HttpClient client = CreateHttpClient(handler))
            {
                Uri uri = Configuration.Http.RemoteHttp11Server.NegotiateAuthUriForDefaultCreds;
                _output.WriteLine("Uri: {0}", uri);
                using (HttpResponseMessage response = await client.GetAsync(uri))
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                }
            }
        }

        [Fact]
        public void Properties_Get_CountIsZero()
        {
            using (HttpClientHandler handler = CreateHttpClientHandler())
            {
                IDictionary<string, object> dict = handler.Properties;
                Assert.Same(dict, handler.Properties);
                Assert.Equal(0, dict.Count);
            }
        }

        [Fact]
        public void Properties_AddItemToDictionary_ItemPresent()
        {
            using (HttpClientHandler handler = CreateHttpClientHandler())
            {
                IDictionary<string, object> dict = handler.Properties;

                var item = new object();
                dict.Add("item", item);

                object value;
                Assert.True(dict.TryGetValue("item", out value));
                Assert.Equal(item, value);
            }
        }

        [OuterLoop("Uses external servers")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task SendAsync_SimpleGet_Success(Configuration.Http.RemoteServer remoteServer)
        {
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            using (HttpResponseMessage response = await client.GetAsync(remoteServer.EchoUri))
            {
                string responseContent = await response.Content.ReadAsStringAsync();
                _output.WriteLine(responseContent);
                TestHelper.VerifyResponseBody(
                    responseContent,
                    response.Content.Headers.ContentMD5,
                    false,
                    null);
            }
        }

        [Fact]
        public async Task GetAsync_IPv6LinkLocalAddressUri_Success()
        {
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }


            using HttpClientHandler handler = CreateHttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = TestHelper.AllowAllCertificates;

            using HttpClient client = CreateHttpClient(handler);

            var options = new GenericLoopbackOptions { Address = TestHelper.GetIPv6LinkLocalAddress() };
            if (options.Address == null)
            {
                throw new SkipTestException("Unable to find valid IPv6 LL address.");
            }

            await LoopbackServerFactory.CreateServerAsync(async (server, url) =>
            {
                _output.WriteLine(url.ToString());
                await TestHelper.WhenAllCompletedOrAnyFailed(
                    server.AcceptConnectionSendResponseAndCloseAsync(),
                    client.SendAsync(CreateRequest(HttpMethod.Get, url, UseVersion, true)));
            }, options: options);
        }

        [Theory]
        [MemberData(nameof(GetAsync_IPBasedUri_Success_MemberData))]
        public async Task GetAsync_IPBasedUri_Success(IPAddress address)
        {
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            using HttpClientHandler handler = CreateHttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = TestHelper.AllowAllCertificates;

            using HttpClient client = CreateHttpClient(handler);

            var options = new GenericLoopbackOptions { Address = address };

            await LoopbackServerFactory.CreateServerAsync(async (server, url) =>
            {
                _output.WriteLine(url.ToString());
                await TestHelper.WhenAllCompletedOrAnyFailed(
                    server.AcceptConnectionSendResponseAndCloseAsync(),
                    client.SendAsync(CreateRequest(HttpMethod.Get, url, UseVersion, true)));
            }, options: options);
        }

        public static IEnumerable<object[]> GetAsync_IPBasedUri_Success_MemberData()
        {
            foreach (var addr in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                if (addr != null)
                {
                    yield return new object[] { addr };
                }
            }
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task SendAsync_MultipleRequestsReusingSameClient_Success(Configuration.Http.RemoteServer remoteServer)
        {
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            {
                for (int i = 0; i < 3; i++)
                {
                    using (HttpResponseMessage response = await client.GetAsync(remoteServer.EchoUri))
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    }
                }
            }
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task GetAsync_ResponseContentAfterClientAndHandlerDispose_Success(Configuration.Http.RemoteServer remoteServer)
        {
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            using (HttpResponseMessage response = await client.GetAsync(remoteServer.EchoUri))
            {
                client.Dispose();
                Assert.NotNull(response);
                string responseContent = await response.Content.ReadAsStringAsync();
                _output.WriteLine(responseContent);
                TestHelper.VerifyResponseBody(responseContent, response.Content.Headers.ContentMD5, false, null);
            }
        }

        [Theory]
        [InlineData("[::1234]")]
        [InlineData("[::1234]:8080")]
        public async Task GetAsync_IPv6AddressInHostHeader_CorrectlyFormatted(string host)
        {
            string ipv6Address = "http://" + host;
            bool connectionAccepted = false;

            await LoopbackServer.CreateClientAndServerAsync(async proxyUri =>
            {
                using (HttpClientHandler handler = CreateHttpClientHandler())
                using (HttpClient client = CreateHttpClient(handler))
                {
                    handler.Proxy = new WebProxy(proxyUri);
                    try { await client.GetAsync(ipv6Address); } catch { }
                }
            }, server => server.AcceptConnectionAsync(async connection =>
            {
                connectionAccepted = true;
                List<string> headers = await connection.ReadRequestHeaderAndSendResponseAsync();
                Assert.Contains($"Host: {host}", headers);
            }));

            Assert.True(connectionAccepted);
        }

        [Theory]
        [InlineData("1.2.3.4")]
        [InlineData("1.2.3.4:8080")]
        [InlineData("[::1234]")]
        [InlineData("[::1234]:8080")]
        public async Task ProxiedIPAddressRequest_NotDefaultPort_CorrectlyFormatted(string host)
        {
            string uri = "http://" + host;
            bool connectionAccepted = false;

            await LoopbackServer.CreateClientAndServerAsync(async proxyUri =>
            {
                using (HttpClientHandler handler = CreateHttpClientHandler())
                using (HttpClient client = CreateHttpClient(handler))
                {
                    handler.Proxy = new WebProxy(proxyUri);
                    try { await client.GetAsync(uri); } catch { }
                }
            }, server => server.AcceptConnectionAsync(async connection =>
            {
                connectionAccepted = true;
                List<string> headers = await connection.ReadRequestHeaderAndSendResponseAsync();
                Assert.Contains($"GET {uri}/ HTTP/1.1", headers);
            }));

            Assert.True(connectionAccepted);
        }

        public static IEnumerable<object[]> DestinationHost_MemberData()
        {
            yield return new object[] { Configuration.Http.Host };
            yield return new object[] { "1.2.3.4" };
            yield return new object[] { "[::1234]" };
        }

        [Theory]
        [OuterLoop("Uses external server")]
        [MemberData(nameof(DestinationHost_MemberData))]
        public async Task ProxiedRequest_DefaultPort_PortStrippedOffInUri(string host)
        {
            string addressUri = $"http://{host}:{HttpDefaultPort}/";
            string expectedAddressUri = $"http://{host}/";
            bool connectionAccepted = false;

            await LoopbackServer.CreateClientAndServerAsync(async proxyUri =>
            {
                using (HttpClientHandler handler = CreateHttpClientHandler())
                using (HttpClient client = CreateHttpClient(handler))
                {
                    handler.Proxy = new WebProxy(proxyUri);
                    try { await client.GetAsync(addressUri); } catch { }
                }
            }, server => server.AcceptConnectionAsync(async connection =>
            {
                connectionAccepted = true;
                List<string> headers = await connection.ReadRequestHeaderAndSendResponseAsync();
                Assert.Contains($"GET {expectedAddressUri} HTTP/1.1", headers);
            }));

            Assert.True(connectionAccepted);
        }

        [Fact]
        [OuterLoop("Uses external server")]
        public async Task ProxyTunnelRequest_PortSpecified_NotStrippedOffInUri()
        {
            // Https proxy request will use CONNECT tunnel, even the default 443 port is specified, it will not be stripped off.
            string requestTarget = $"{Configuration.Http.SecureHost}:443";
            string addressUri = $"https://{requestTarget}/";
            bool connectionAccepted = false;

            await LoopbackServer.CreateClientAndServerAsync(async proxyUri =>
            {
                using (HttpClientHandler handler = CreateHttpClientHandler())
                using (HttpClient client = CreateHttpClient(handler))
                {
                    handler.Proxy = new WebProxy(proxyUri);
                    handler.ServerCertificateCustomValidationCallback = TestHelper.AllowAllCertificates;
                    try { await client.GetAsync(addressUri); } catch { }
                }
            }, server => server.AcceptConnectionAsync(async connection =>
            {
                connectionAccepted = true;
                List<string> headers = await connection.ReadRequestHeaderAndSendResponseAsync();
                Assert.Contains($"CONNECT {requestTarget} HTTP/1.1", headers);
            }));

            Assert.True(connectionAccepted);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        [OuterLoop("Uses external server")]
        public async Task ProxyTunnelRequest_UserAgentHeaderAdded(bool addUserAgentHeader)
        {
            if (IsWinHttpHandler)
            {
                return; // Skip test since the fix is only in SocketsHttpHandler.
            }

            string addressUri = $"https://{Configuration.Http.SecureHost}/";
            bool connectionAccepted = false;

            await LoopbackServer.CreateClientAndServerAsync(async proxyUri =>
            {
                using (HttpClientHandler handler = CreateHttpClientHandler())
                using (var client = new HttpClient(handler))
                {
                    handler.Proxy = new WebProxy(proxyUri);
                    handler.ServerCertificateCustomValidationCallback = TestHelper.AllowAllCertificates;
                    if (addUserAgentHeader)
                    {
                        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
                    }
                    try
                    {
                        await client.GetAsync(addressUri);
                    }
                    catch
                    {
                    }
                }
            }, server => server.AcceptConnectionAsync(async connection =>
            {
                connectionAccepted = true;
                List<string> headers = await connection.ReadRequestHeaderAndSendResponseAsync();
                Assert.Contains($"CONNECT {Configuration.Http.SecureHost}:443 HTTP/1.1", headers);
                if (addUserAgentHeader)
                {
                    Assert.Contains("User-Agent: Mozilla/5.0", headers);
                }
                else
                {
                    Assert.DoesNotContain("User-Agent:", headers);
                }
            }));

            Assert.True(connectionAccepted);
        }

        public static IEnumerable<object[]> SecureAndNonSecure_IPBasedUri_MemberData() =>
            from address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
            from useSsl in BoolValues
            select new object[] { address, useSsl };

        [Theory]
        [MemberData(nameof(SecureAndNonSecure_IPBasedUri_MemberData))]
        public async Task GetAsync_SecureAndNonSecureIPBasedUri_CorrectlyFormatted(IPAddress address, bool useSsl)
        {
            if (LoopbackServerFactory.Version >= HttpVersion20.Value)
            {
                // Host header is not supported on HTTP/2 and later.
                return;
            }

            var options = new LoopbackServer.Options { Address = address, UseSsl= useSsl };
            bool connectionAccepted = false;
            string host = "";

            await LoopbackServer.CreateClientAndServerAsync(async url =>
            {
                host = $"{url.Host}:{url.Port}";
                using (HttpClientHandler handler = CreateHttpClientHandler())
                using (HttpClient client = CreateHttpClient(handler))
                {
                    if (useSsl)
                    {
                        handler.ServerCertificateCustomValidationCallback = TestHelper.AllowAllCertificates;
                    }
                    try { await client.GetAsync(url); } catch { }
                }
            }, server => server.AcceptConnectionAsync(async connection =>
            {
                connectionAccepted = true;
                List<string> headers = await connection.ReadRequestHeaderAndSendResponseAsync();
                Assert.Contains($"Host: {host}", headers);
            }), options);

            Assert.True(connectionAccepted);
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task GetAsync_ServerNeedsBasicAuthAndSetDefaultCredentials_StatusCodeUnauthorized(Configuration.Http.RemoteServer remoteServer)
        {
            HttpClientHandler handler = CreateHttpClientHandler();
            handler.Credentials = CredentialCache.DefaultCredentials;
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer, handler))
            {
                Uri uri = remoteServer.BasicAuthUriForCreds(userName: Username, password: Password);
                using (HttpResponseMessage response = await client.GetAsync(uri))
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                }
            }
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task GetAsync_ServerNeedsAuthAndSetCredential_StatusCodeOK(Configuration.Http.RemoteServer remoteServer)
        {
            HttpClientHandler handler = CreateHttpClientHandler();
            handler.Credentials = _credential;
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer, handler))
            {
                Uri uri = remoteServer.BasicAuthUriForCreds(userName: Username, password: Password);
                using (HttpResponseMessage response = await client.GetAsync(uri))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }
            }
        }

        [OuterLoop("Uses external server")]
        [Fact]
        public async Task GetAsync_ServerNeedsAuthAndNoCredential_StatusCodeUnauthorized()
        {
            using (HttpClient client = CreateHttpClient(UseVersion.ToString()))
            {
                Uri uri = Configuration.Http.RemoteHttp11Server.BasicAuthUriForCreds(userName: Username, password: Password);
                using (HttpResponseMessage response = await client.GetAsync(uri))
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                }
            }
        }

        [Theory]
        [InlineData("WWW-Authenticate", "CustomAuth")]
        [InlineData("", "")] // RFC7235 requires servers to send this header with 401 but some servers don't.
        public async Task GetAsync_ServerNeedsNonStandardAuthAndSetCredential_StatusCodeUnauthorized(string authHeadrName, string authHeaderValue)
        {
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            await LoopbackServerFactory.CreateServerAsync(async (server, url) =>
            {
                HttpClientHandler handler = CreateHttpClientHandler();
                handler.Credentials = new NetworkCredential("unused", "PLACEHOLDER");
                using (HttpClient client = CreateHttpClient(handler))
                {
                    Task<HttpResponseMessage> getResponseTask = client.GetAsync(url);

                    Task<HttpRequestData> serverTask = server.AcceptConnectionSendResponseAndCloseAsync(HttpStatusCode.Unauthorized, additionalHeaders: string.IsNullOrEmpty(authHeadrName) ? null : new HttpHeaderData[] { new HttpHeaderData(authHeadrName, authHeaderValue) });

                    await TestHelper.WhenAllCompletedOrAnyFailed(getResponseTask, serverTask);
                    using (HttpResponseMessage response = await getResponseTask)
                    {
                        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                    }
                }
            });
        }

        [OuterLoop("Uses external server")]
        [Theory]
        [MemberData(nameof(RemoteServersAndHeaderEchoUrisMemberData))]
        public async Task GetAsync_RequestHeadersAddCustomHeaders_HeaderAndEmptyValueSent(Configuration.Http.RemoteServer remoteServer, Uri uri)
        {
            if (IsWinHttpHandler && !PlatformDetection.IsWindows10Version1709OrGreater)
            {
                return;
            }

            string name = "X-Cust-Header-NoValue";
            string value = "";
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            {
                _output.WriteLine($"name={name}, value={value}");
                client.DefaultRequestHeaders.Add(name, value);
                using (HttpResponseMessage httpResponse = await client.GetAsync(uri))
                {
                    Assert.Equal(HttpStatusCode.OK, httpResponse.StatusCode);
                    string responseText = await httpResponse.Content.ReadAsStringAsync();
                    _output.WriteLine(responseText);
                    Assert.True(TestHelper.JsonMessageContainsKeyValue(responseText, name, value));
                }
            }
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(RemoteServersHeaderValuesAndUris))]
        public async Task GetAsync_RequestHeadersAddCustomHeaders_HeaderAndValueSent(Configuration.Http.RemoteServer remoteServer, string name, string value, Uri uri)
        {
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            {
                _output.WriteLine($"name={name}, value={value}");
                client.DefaultRequestHeaders.Add(name, value);
                using (HttpResponseMessage httpResponse = await client.GetAsync(uri))
                {
                    Assert.Equal(HttpStatusCode.OK, httpResponse.StatusCode);
                    string responseText = await httpResponse.Content.ReadAsStringAsync();
                    _output.WriteLine(responseText);
                    Assert.True(TestHelper.JsonMessageContainsKeyValue(responseText, name, value));
                }
            }
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(RemoteServersAndHeaderEchoUrisMemberData))]
        public async Task GetAsync_LargeRequestHeader_HeadersAndValuesSent(Configuration.Http.RemoteServer remoteServer, Uri uri)
        {
            // Unfortunately, our remote servers seem to have pretty strict limits (around 16K?)
            // on the total size of the request header.
            // TODO: Figure out how to reconfigure remote endpoints to allow larger request headers,
            // and then increase the limits in this test.

            string headerValue = new string('a', 2048);
            const int headerCount = 6;

            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            {
                for (int i = 0; i < headerCount; i++)
                {
                    client.DefaultRequestHeaders.Add($"Header-{i}", headerValue);
                }

                using (HttpResponseMessage httpResponse = await client.GetAsync(uri))
                {
                    Assert.Equal(HttpStatusCode.OK, httpResponse.StatusCode);
                    string responseText = await httpResponse.Content.ReadAsStringAsync();

                    for (int i = 0; i < headerCount; i++)
                    {
                        Assert.True(TestHelper.JsonMessageContainsKeyValue(responseText, $"Header-{i}", headerValue));
                    }
                }
            }
        }

        public static IEnumerable<object[]> RemoteServersHeaderValuesAndUris()
        {
            foreach ((Configuration.Http.RemoteServer remoteServer, Uri uri) in RemoteServersAndHeaderEchoUris())
            {
                yield return new object[] { remoteServer, "X-CustomHeader", "x-value", uri };
                yield return new object[] { remoteServer, "MyHeader", "1, 2, 3", uri };

                // Construct a header value with every valid character (except space)
                string allchars = "";
                for (int i = 0x21; i <= 0x7E; i++)
                {
                    allchars = allchars + (char)i;
                }

                // Put a space in the middle so it's not interpreted as insignificant leading/trailing whitespace
                allchars = allchars + " " + allchars;

                yield return new object[] { remoteServer, "All-Valid-Chars-Header", allchars, uri };
            }
        }

        public static IEnumerable<(Configuration.Http.RemoteServer remoteServer, Uri uri)> RemoteServersAndHeaderEchoUris()
        {
            foreach (Configuration.Http.RemoteServer remoteServer in Configuration.Http.RemoteServers)
            {
                yield return (remoteServer, remoteServer.EchoUri);
                yield return (remoteServer, remoteServer.RedirectUriForDestinationUri(
                    statusCode: 302,
                    destinationUri: remoteServer.EchoUri,
                    hops: 1));
            }
        }

        public static IEnumerable<object[]> RemoteServersAndHeaderEchoUrisMemberData() => RemoteServersAndHeaderEchoUris().Select(x => new object[] { x.remoteServer, x.uri });

        [Theory]
        [InlineData(":")]
        [InlineData("\x1234: \x5678")]
        [InlineData("nocolon")]
        [InlineData("no colon")]
        [InlineData("Content-Length      ")]
        public async Task GetAsync_InvalidHeaderNameValue_ThrowsHttpRequestException(string invalidHeader)
        {
            await LoopbackServer.CreateClientAndServerAsync(async uri =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(uri));
                }
            }, server => server.AcceptConnectionSendCustomResponseAndCloseAsync($"HTTP/1.1 200 OK\r\n{invalidHeader}\r\nContent-Length: 11\r\n\r\nhello world"));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task GetAsync_IncompleteData_ThrowsHttpRequestException(bool failDuringHeaders, bool getString)
        {
            if (IsWinHttpHandler)
            {
                return; // see https://github.com/dotnet/runtime/issues/30115#issuecomment-508330958
            }

            await LoopbackServer.CreateClientAndServerAsync(async uri =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    Task t = getString ? (Task)
                        client.GetStringAsync(uri) :
                        client.GetByteArrayAsync(uri);
                    await Assert.ThrowsAsync<HttpRequestException>(() => t);
                }
            }, server =>
                failDuringHeaders ?
                   server.AcceptConnectionSendCustomResponseAndCloseAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n") :
                   server.AcceptConnectionSendCustomResponseAndCloseAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhe"));
        }

        [Fact]
        public async Task PostAsync_ManyDifferentRequestHeaders_SentCorrectly()
        {
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            const string content = "hello world";

            // Using examples from https://en.wikipedia.org/wiki/List_of_HTTP_header_fields#Request_fields
            // Exercises all exposed request.Headers and request.Content.Headers strongly-typed properties
            await LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    byte[] contentArray = Encoding.ASCII.GetBytes(content);
                    var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(contentArray), Version = UseVersion };

                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
                    request.Headers.AcceptCharset.Add(new StringWithQualityHeaderValue("utf-8"));
                    request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
                    request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
                    request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
                    request.Headers.Add("Accept-Datetime", "Thu, 31 May 2007 20:35:00 GMT");
                    request.Headers.Add("Access-Control-Request-Method", "GET");
                    request.Headers.Add("Access-Control-Request-Headers", "GET");
                    request.Headers.Add("Age", "12");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "QWxhZGRpbjpvcGVuIHNlc2FtZQ==");
                    request.Headers.CacheControl = new CacheControlHeaderValue() { NoCache = true };
                    request.Headers.Connection.Add("close");
                    request.Headers.Add("Cookie", "$Version=1; Skin=new");
                    request.Content.Headers.ContentLength = contentArray.Length;
                    request.Content.Headers.ContentMD5 = MD5.Create().ComputeHash(contentArray);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
                    request.Headers.Date = DateTimeOffset.Parse("Tue, 15 Nov 1994 08:12:31 GMT");
                    request.Headers.Expect.Add(new NameValueWithParametersHeaderValue("100-continue"));
                    request.Headers.Add("Forwarded", "for=192.0.2.60;proto=http;by=203.0.113.43");
                    request.Headers.Add("From", "User Name <user@example.com>");
                    request.Headers.Host = "en.wikipedia.org:8080";
                    request.Headers.IfMatch.Add(new EntityTagHeaderValue("\"37060cd8c284d8af7ad3082f209582d\""));
                    request.Headers.IfModifiedSince = DateTimeOffset.Parse("Sat, 29 Oct 1994 19:43:31 GMT");
                    request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"737060cd8c284d8af7ad3082f209582d\""));
                    request.Headers.IfRange = new RangeConditionHeaderValue(DateTimeOffset.Parse("Wed, 21 Oct 2015 07:28:00 GMT"));
                    request.Headers.IfUnmodifiedSince = DateTimeOffset.Parse("Sat, 29 Oct 1994 19:43:31 GMT");
                    request.Headers.MaxForwards = 10;
                    request.Headers.Add("Origin", "http://www.example-social-network.com");
                    request.Headers.Pragma.Add(new NameValueHeaderValue("no-cache"));
                    request.Headers.ProxyAuthorization = new AuthenticationHeaderValue("Basic", "QWxhZGRpbjpvcGVuIHNlc2FtZQ==");
                    request.Headers.Range = new RangeHeaderValue(500, 999);
                    request.Headers.Referrer = new Uri("http://en.wikipedia.org/wiki/Main_Page");
                    request.Headers.TE.Add(new TransferCodingWithQualityHeaderValue("trailers"));
                    request.Headers.TE.Add(new TransferCodingWithQualityHeaderValue("deflate"));
                    request.Headers.Trailer.Add("MyTrailer");
                    request.Headers.TransferEncoding.Add(new TransferCodingHeaderValue("chunked"));
                    request.Headers.UserAgent.Add(new ProductInfoHeaderValue(new ProductHeaderValue("Mozilla", "5.0")));
                    request.Headers.Upgrade.Add(new ProductHeaderValue("HTTPS", "1.3"));
                    request.Headers.Upgrade.Add(new ProductHeaderValue("IRC", "6.9"));
                    request.Headers.Upgrade.Add(new ProductHeaderValue("RTA", "x11"));
                    request.Headers.Upgrade.Add(new ProductHeaderValue("websocket"));
                    request.Headers.Via.Add(new ViaHeaderValue("1.0", "fred"));
                    request.Headers.Via.Add(new ViaHeaderValue("1.1", "example.com", null, "(Apache/1.1)"));
                    request.Headers.Warning.Add(new WarningHeaderValue(199, "-", "\"Miscellaneous warning\""));
                    request.Headers.Add("X-Requested-With", "XMLHttpRequest");
                    request.Headers.Add("DNT", "1 (Do Not Track Enabled)");
                    request.Headers.Add("X-Forwarded-For", "client1");
                    request.Headers.Add("X-Forwarded-For", "proxy1");
                    request.Headers.Add("X-Forwarded-For", "proxy2");
                    request.Headers.Add("X-Forwarded-Host", "en.wikipedia.org:8080");
                    request.Headers.Add("X-Forwarded-Proto", "https");
                    request.Headers.Add("Front-End-Https", "https");
                    request.Headers.Add("X-Http-Method-Override", "DELETE");
                    request.Headers.Add("X-ATT-DeviceId", "GT-P7320/P7320XXLPG");
                    request.Headers.Add("X-Wap-Profile", "http://wap.samsungmobile.com/uaprof/SGH-I777.xml");
                    request.Headers.Add("Proxy-Connection", "keep-alive");
                    request.Headers.Add("X-UIDH", "...");
                    request.Headers.Add("X-Csrf-Token", "i8XNjC4b8KVok4uw5RftR38Wgp2BFwql");
                    request.Headers.Add("X-Request-ID", "f058ebd6-02f7-4d3f-942e-904344e8cde5");
                    request.Headers.Add("X-Request-ID", "f058ebd6-02f7-4d3f-942e-904344e8cde5");
                    request.Headers.Add("X-Empty", "");
                    request.Headers.Add("X-Null", (string)null);
                    request.Headers.Add("X-Underscore_Name", "X-Underscore_Name");
                    request.Headers.Add("X-End", "End");

                    (await client.SendAsync(TestAsync, request, HttpCompletionOption.ResponseHeadersRead)).Dispose();
                }
            }, async server =>
            {
                {
                    HttpRequestData requestData = await server.HandleRequestAsync(HttpStatusCode.OK);

                    var headersSet = requestData.Headers;

                    Assert.Equal(content, Encoding.ASCII.GetString(requestData.Body));

                    Assert.Equal("utf-8", requestData.GetSingleHeaderValue("Accept-Charset"));
                    Assert.Equal("gzip, deflate", requestData.GetSingleHeaderValue("Accept-Encoding"));
                    Assert.Equal("en-US", requestData.GetSingleHeaderValue("Accept-Language"));
                    Assert.Equal("Thu, 31 May 2007 20:35:00 GMT", requestData.GetSingleHeaderValue("Accept-Datetime"));
                    Assert.Equal("GET", requestData.GetSingleHeaderValue("Access-Control-Request-Method"));
                    Assert.Equal("GET", requestData.GetSingleHeaderValue("Access-Control-Request-Headers"));
                    Assert.Equal("12", requestData.GetSingleHeaderValue("Age"));
                    Assert.Equal("Basic QWxhZGRpbjpvcGVuIHNlc2FtZQ==", requestData.GetSingleHeaderValue("Authorization"));
                    Assert.Equal("no-cache", requestData.GetSingleHeaderValue("Cache-Control"));
                    Assert.Equal("$Version=1; Skin=new", requestData.GetSingleHeaderValue("Cookie"));
                    Assert.Equal("Tue, 15 Nov 1994 08:12:31 GMT", requestData.GetSingleHeaderValue("Date"));
                    Assert.Equal("100-continue", requestData.GetSingleHeaderValue("Expect"));
                    Assert.Equal("for=192.0.2.60;proto=http;by=203.0.113.43", requestData.GetSingleHeaderValue("Forwarded"));
                    Assert.Equal("User Name <user@example.com>", requestData.GetSingleHeaderValue("From"));
                    Assert.Equal("\"37060cd8c284d8af7ad3082f209582d\"", requestData.GetSingleHeaderValue("If-Match"));
                    Assert.Equal("Sat, 29 Oct 1994 19:43:31 GMT", requestData.GetSingleHeaderValue("If-Modified-Since"));
                    Assert.Equal("\"737060cd8c284d8af7ad3082f209582d\"", requestData.GetSingleHeaderValue("If-None-Match"));
                    Assert.Equal("Wed, 21 Oct 2015 07:28:00 GMT", requestData.GetSingleHeaderValue("If-Range"));
                    Assert.Equal("Sat, 29 Oct 1994 19:43:31 GMT", requestData.GetSingleHeaderValue("If-Unmodified-Since"));
                    Assert.Equal("10", requestData.GetSingleHeaderValue("Max-Forwards"));
                    Assert.Equal("http://www.example-social-network.com", requestData.GetSingleHeaderValue("Origin"));
                    Assert.Equal("no-cache", requestData.GetSingleHeaderValue("Pragma"));
                    Assert.Equal("Basic QWxhZGRpbjpvcGVuIHNlc2FtZQ==", requestData.GetSingleHeaderValue("Proxy-Authorization"));
                    Assert.Equal("bytes=500-999", requestData.GetSingleHeaderValue("Range"));
                    Assert.Equal("http://en.wikipedia.org/wiki/Main_Page", requestData.GetSingleHeaderValue("Referer"));
                    Assert.Equal("MyTrailer", requestData.GetSingleHeaderValue("Trailer"));
                    Assert.Equal("Mozilla/5.0", requestData.GetSingleHeaderValue("User-Agent"));
                    Assert.Equal("1.0 fred, 1.1 example.com (Apache/1.1)", requestData.GetSingleHeaderValue("Via"));
                    Assert.Equal("199 - \"Miscellaneous warning\"", requestData.GetSingleHeaderValue("Warning"));
                    Assert.Equal("XMLHttpRequest", requestData.GetSingleHeaderValue("X-Requested-With"));
                    Assert.Equal("1 (Do Not Track Enabled)", requestData.GetSingleHeaderValue("DNT"));
                    Assert.Equal("client1, proxy1, proxy2", requestData.GetSingleHeaderValue("X-Forwarded-For"));
                    Assert.Equal("en.wikipedia.org:8080", requestData.GetSingleHeaderValue("X-Forwarded-Host"));
                    Assert.Equal("https", requestData.GetSingleHeaderValue("X-Forwarded-Proto"));
                    Assert.Equal("https", requestData.GetSingleHeaderValue("Front-End-Https"));
                    Assert.Equal("DELETE", requestData.GetSingleHeaderValue("X-Http-Method-Override"));
                    Assert.Equal("GT-P7320/P7320XXLPG", requestData.GetSingleHeaderValue("X-ATT-DeviceId"));
                    Assert.Equal("http://wap.samsungmobile.com/uaprof/SGH-I777.xml", requestData.GetSingleHeaderValue("X-Wap-Profile"));
                    Assert.Equal("...", requestData.GetSingleHeaderValue("X-UIDH"));
                    Assert.Equal("i8XNjC4b8KVok4uw5RftR38Wgp2BFwql", requestData.GetSingleHeaderValue("X-Csrf-Token"));
                    Assert.Equal("f058ebd6-02f7-4d3f-942e-904344e8cde5, f058ebd6-02f7-4d3f-942e-904344e8cde5", requestData.GetSingleHeaderValue("X-Request-ID"));
                    Assert.Equal("", requestData.GetSingleHeaderValue("X-Null"));
                    Assert.Equal("", requestData.GetSingleHeaderValue("X-Empty"));
                    Assert.Equal("X-Underscore_Name", requestData.GetSingleHeaderValue("X-Underscore_Name"));
                    Assert.Equal("End", requestData.GetSingleHeaderValue("X-End"));

                    if (LoopbackServerFactory.Version >= HttpVersion20.Value)
                    {
                        // HTTP/2 and later forbids certain headers or values.
                        Assert.Equal("trailers", requestData.GetSingleHeaderValue("TE"));
                        Assert.Equal(0, requestData.GetHeaderValueCount("Upgrade"));
                        Assert.Equal(0, requestData.GetHeaderValueCount("Proxy-Connection"));
                        Assert.Equal(0, requestData.GetHeaderValueCount("Host"));
                        Assert.Equal(0, requestData.GetHeaderValueCount("Connection"));
                        Assert.Equal(0, requestData.GetHeaderValueCount("Transfer-Encoding"));
                    }
                    else
                    {
                        // Verify HTTP/1.x headers
                        Assert.Equal("close", requestData.GetSingleHeaderValue("Connection"), StringComparer.OrdinalIgnoreCase); // NetFxHandler uses "Close" vs "close"
                        Assert.Equal("en.wikipedia.org:8080", requestData.GetSingleHeaderValue("Host"));
                        Assert.Equal("trailers, deflate", requestData.GetSingleHeaderValue("TE"));
                        Assert.Equal("HTTPS/1.3, IRC/6.9, RTA/x11, websocket", requestData.GetSingleHeaderValue("Upgrade"));
                        Assert.Equal("keep-alive", requestData.GetSingleHeaderValue("Proxy-Connection"));
                    }
                }
            });
        }

        public static IEnumerable<object[]> GetAsync_ManyDifferentResponseHeaders_ParsedCorrectly_MemberData() =>
            from newline in new[] { "\n", "\r\n" }
            from fold in new[] { "", newline + " ", newline + "\t", newline + "    " }
            from dribble in new[] { false, true }
            select new object[] { newline, fold, dribble };

        [Theory]
        [MemberData(nameof(GetAsync_ManyDifferentResponseHeaders_ParsedCorrectly_MemberData))]
        public async Task GetAsync_ManyDifferentResponseHeaders_ParsedCorrectly(string newline, string fold, bool dribble)
        {
            if (LoopbackServerFactory.Version >= HttpVersion20.Value)
            {
                // Folding is not supported on HTTP/2 and later.
                return;
            }

            // Using examples from https://en.wikipedia.org/wiki/List_of_HTTP_header_fields#Response_fields
            // Exercises all exposed response.Headers and response.Content.Headers strongly-typed properties
            await LoopbackServer.CreateClientAndServerAsync(async uri =>
            {
                using (HttpClient client = CreateHttpClient())
                using (HttpResponseMessage resp = await client.GetAsync(uri))
                {
                    Assert.Equal("1.1", resp.Version.ToString());
                    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                    Assert.Contains("*", resp.Headers.GetValues("Access-Control-Allow-Origin"));
                    Assert.Contains("text/example;charset=utf-8", resp.Headers.GetValues("Accept-Patch"));
                    Assert.Contains("bytes", resp.Headers.AcceptRanges);
                    Assert.Equal(TimeSpan.FromSeconds(12), resp.Headers.Age.GetValueOrDefault());
                    Assert.Contains("Bearer 63123a47139a49829bcd8d03005ca9d7", resp.Headers.GetValues("Authorization"));
                    Assert.Contains("GET", resp.Content.Headers.Allow);
                    Assert.Contains("HEAD", resp.Content.Headers.Allow);
                    Assert.Contains("http/1.1=\"http2.example.com:8001\"; ma=7200", resp.Headers.GetValues("Alt-Svc"));
                    Assert.Equal(TimeSpan.FromSeconds(3600), resp.Headers.CacheControl.MaxAge.GetValueOrDefault());
                    Assert.Contains("close", resp.Headers.Connection);
                    Assert.True(resp.Headers.ConnectionClose.GetValueOrDefault());
                    Assert.Equal("attachment", resp.Content.Headers.ContentDisposition.DispositionType);
                    Assert.Equal("\"fname.ext\"", resp.Content.Headers.ContentDisposition.FileName);
                    Assert.Contains("gzip", resp.Content.Headers.ContentEncoding);
                    Assert.Contains("da", resp.Content.Headers.ContentLanguage);
                    Assert.Equal(new Uri("/index.htm", UriKind.Relative), resp.Content.Headers.ContentLocation);
                    Assert.Equal(Convert.FromBase64String("Q2hlY2sgSW50ZWdyaXR5IQ=="), resp.Content.Headers.ContentMD5);
                    Assert.Equal("bytes", resp.Content.Headers.ContentRange.Unit);
                    Assert.Equal(21010, resp.Content.Headers.ContentRange.From.GetValueOrDefault());
                    Assert.Equal(47021, resp.Content.Headers.ContentRange.To.GetValueOrDefault());
                    Assert.Equal(47022, resp.Content.Headers.ContentRange.Length.GetValueOrDefault());
                    Assert.Equal("text/html", resp.Content.Headers.ContentType.MediaType);
                    Assert.Equal("utf-8", resp.Content.Headers.ContentType.CharSet);
                    Assert.Equal(DateTimeOffset.Parse("Tue, 15 Nov 1994 08:12:31 GMT"), resp.Headers.Date.GetValueOrDefault());
                    Assert.Equal("\"737060cd8c284d8af7ad3082f209582d\"", resp.Headers.ETag.Tag);
                    Assert.Equal(DateTimeOffset.Parse("Thu, 01 Dec 1994 16:00:00 GMT"), resp.Content.Headers.Expires.GetValueOrDefault());
                    Assert.Equal(DateTimeOffset.Parse("Tue, 15 Nov 1994 12:45:26 GMT"), resp.Content.Headers.LastModified.GetValueOrDefault());
                    Assert.Contains("</feed>; rel=\"alternate\"", resp.Headers.GetValues("Link"));
                    Assert.Equal(new Uri("http://www.w3.org/pub/WWW/People.html"), resp.Headers.Location);
                    Assert.Contains("CP=\"This is not a P3P policy!\"", resp.Headers.GetValues("P3P"));
                    Assert.Contains(new NameValueHeaderValue("no-cache"), resp.Headers.Pragma);
                    Assert.Contains(new AuthenticationHeaderValue("basic"), resp.Headers.ProxyAuthenticate);
                    Assert.Contains("max-age=2592000; pin-sha256=\"E9CZ9INDbd+2eRQozYqqbQ2yXLVKB9+xcprMF+44U1g=\"", resp.Headers.GetValues("Public-Key-Pins"));
                    Assert.Equal(TimeSpan.FromSeconds(120), resp.Headers.RetryAfter.Delta.GetValueOrDefault());
                    Assert.Contains(new ProductInfoHeaderValue("Apache", "2.4.1"), resp.Headers.Server);
                    Assert.Contains("UserID=JohnDoe; Max-Age=3600; Version=1", resp.Headers.GetValues("Set-Cookie"));
                    Assert.Contains("max-age=16070400; includeSubDomains", resp.Headers.GetValues("Strict-Transport-Security"));
                    Assert.Contains("Max-Forwards", resp.Headers.Trailer);
                    Assert.Contains("?", resp.Headers.GetValues("Tk"));
                    Assert.Contains(new ProductHeaderValue("HTTPS", "1.3"), resp.Headers.Upgrade);
                    Assert.Contains(new ProductHeaderValue("IRC", "6.9"), resp.Headers.Upgrade);
                    Assert.Contains(new ProductHeaderValue("websocket"), resp.Headers.Upgrade);
                    Assert.Contains("Accept-Language", resp.Headers.Vary);
                    Assert.Contains(new ViaHeaderValue("1.0", "fred"), resp.Headers.Via);
                    Assert.Contains(new ViaHeaderValue("1.1", "example.com", null, "(Apache/1.1)"), resp.Headers.Via);
                    Assert.Contains(new WarningHeaderValue(199, "-", "\"Miscellaneous warning\"", DateTimeOffset.Parse("Wed, 21 Oct 2015 07:28:00 GMT")), resp.Headers.Warning);
                    Assert.Contains(new AuthenticationHeaderValue("Basic"), resp.Headers.WwwAuthenticate);
                    Assert.Contains("deny", resp.Headers.GetValues("X-Frame-Options"), StringComparer.OrdinalIgnoreCase);
                    Assert.Contains("default-src 'self'", resp.Headers.GetValues("X-WebKit-CSP"));
                    Assert.Contains("5; url=http://www.w3.org/pub/WWW/People.html", resp.Headers.GetValues("Refresh"));
                    Assert.Contains("200 OK", resp.Headers.GetValues("Status"));
                    Assert.Contains("<origin>[, <origin>]*", resp.Headers.GetValues("Timing-Allow-Origin"));
                    Assert.Contains("42.666", resp.Headers.GetValues("X-Content-Duration"));
                    Assert.Contains("nosniff", resp.Headers.GetValues("X-Content-Type-Options"));
                    Assert.Contains("PHP/5.4.0", resp.Headers.GetValues("X-Powered-By"));
                    Assert.Contains("f058ebd6-02f7-4d3f-942e-904344e8cde5", resp.Headers.GetValues("X-Request-ID"));
                    Assert.Contains("IE=EmulateIE7", resp.Headers.GetValues("X-UA-Compatible"));
                    Assert.Contains("1; mode=block", resp.Headers.GetValues("X-XSS-Protection"));
                }
            }, server => server.AcceptConnectionSendCustomResponseAndCloseAsync(
                $"HTTP/1.1 200 OK{newline}" +
                $"Access-Control-Allow-Origin:{fold} *{newline}" +
                $"Accept-Patch:{fold} text/example;charset=utf-8{newline}" +
                $"Accept-Ranges:{fold} bytes{newline}" +
                $"Age: {fold}12{newline}" +
                // [SuppressMessage("Microsoft.Security", "CS002:SecretInNextLine", Justification="Suppression approved. Unit test dummy authorization.")]
                $"Authorization: Bearer 63123a47139a49829bcd8d03005ca9d7{newline}" +
                $"Allow: {fold}GET, HEAD{newline}" +
                $"Alt-Svc:{fold} http/1.1=\"http2.example.com:8001\"; ma=7200{newline}" +
                $"Cache-Control: {fold}max-age=3600{newline}" +
                $"Connection:{fold} close{newline}" +
                $"Content-Disposition: {fold}attachment;{fold} filename=\"fname.ext\"{newline}" +
                $"Content-Encoding: {fold}gzip{newline}" +
                $"Content-Language:{fold} da{newline}" +
                $"Content-Location: {fold}/index.htm{newline}" +
                $"Content-MD5:{fold} Q2hlY2sgSW50ZWdyaXR5IQ=={newline}" +
                $"Content-Range: {fold}bytes {fold}21010-47021/47022{newline}" +
                $"Content-Type: text/html;{fold} charset=utf-8{newline}" +
                $"Date: Tue, 15 Nov 1994{fold} 08:12:31 GMT{newline}" +
                $"ETag: {fold}\"737060cd8c284d8af7ad3082f209582d\"{newline}" +
                $"Expires: Thu,{fold} 01 Dec 1994 16:00:00 GMT{newline}" +
                $"Last-Modified:{fold} Tue, 15 Nov 1994 12:45:26 GMT{newline}" +
                $"Link:{fold} </feed>; rel=\"alternate\"{newline}" +
                $"Location:{fold} http://www.w3.org/pub/WWW/People.html{newline}" +
                $"P3P: {fold}CP=\"This is not a P3P policy!\"{newline}" +
                $"Pragma: {fold}no-cache{newline}" +
                $"Proxy-Authenticate:{fold} Basic{newline}" +
                $"Public-Key-Pins:{fold} max-age=2592000; pin-sha256=\"E9CZ9INDbd+2eRQozYqqbQ2yXLVKB9+xcprMF+44U1g=\"{newline}" +
                $"Retry-After: {fold}120{newline}" +
                $"Server: {fold}Apache/2.4.1{fold} (Unix){newline}" +
                $"Set-Cookie: {fold}UserID=JohnDoe; Max-Age=3600; Version=1{newline}" +
                $"Strict-Transport-Security: {fold}max-age=16070400; includeSubDomains{newline}" +
                $"Trailer: {fold}Max-Forwards{newline}" +
                $"Tk: {fold}?{newline}" +
                $"Upgrade: HTTPS/1.3,{fold} IRC/6.9,{fold} RTA/x11, {fold}websocket{newline}" +
                $"Vary:{fold} Accept-Language{newline}" +
                $"Via:{fold} 1.0 fred, 1.1 example.com{fold} (Apache/1.1){newline}" +
                $"Warning:{fold} 199 - \"Miscellaneous warning\" \"Wed, 21 Oct 2015 07:28:00 GMT\"{newline}" +
                $"WWW-Authenticate: {fold}Basic{newline}" +
                $"X-Frame-Options: {fold}deny{newline}" +
                $"X-WebKit-CSP: default-src 'self'{newline}" +
                $"Refresh: {fold}5; url=http://www.w3.org/pub/WWW/People.html{newline}" +
                $"Server-Timing: serveroperat{fold}ion;dur=1.23{newline}" +
                $"Status: {fold}200 OK{newline}" +
                $"Timing-Allow-Origin: {fold}<origin>[, <origin>]*{newline}" +
                $"Upgrade-Insecure-Requests:{fold} 1{newline}" +
                $"X-Content-Duration:{fold} 42.666{newline}" +
                $"X-Content-Type-Options: {fold}nosniff{newline}" +
                $"X-Powered-By: {fold}PHP/5.4.0{newline}" +
                $"X-Request-ID:{fold} f058ebd6-02f7-4d3f-942e-904344e8cde5{newline}" +
                $"X-UA-Compatible: {fold}IE=EmulateIE7{newline}" +
                $"X-XSS-Protection:{fold} 1; mode=block{newline}" +
                $"{newline}"),
                dribble ? new LoopbackServer.Options { StreamWrapper = s => new DribbleStream(s) } : null);
        }

        [Fact]
        public async Task GetAsync_NonTraditionalChunkSizes_Accepted()
        {
            if (LoopbackServerFactory.Version >= HttpVersion20.Value)
            {
                // Chunking is not supported on HTTP/2 and later.
                return;
            }

            await LoopbackServer.CreateServerAsync(async (server, url) =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    Task<HttpResponseMessage> getResponseTask = client.GetAsync(url);
                    await TestHelper.WhenAllCompletedOrAnyFailed(
                        getResponseTask,
                        server.AcceptConnectionSendCustomResponseAndCloseAsync(
                            "HTTP/1.1 200 OK\r\n" +
                            "Connection: close\r\n" +
                            "Transfer-Encoding: chunked\r\n" +
                            "\r\n" +
                            "4    \r\n" + // whitespace after size
                            "data\r\n" +
                            "5;somekey=somevalue\r\n" + // chunk extension
                            "hello\r\n" +
                            "7\t ;chunkextension\r\n" + // tabs/spaces then chunk extension
                            "netcore\r\n" +
                            "0\r\n" +
                            "\r\n"));

                    using (HttpResponseMessage response = await getResponseTask)
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        string data = await response.Content.ReadAsStringAsync();
                        Assert.Contains("data", data);
                        Assert.Contains("hello", data);
                        Assert.Contains("netcore", data);
                        Assert.DoesNotContain("somekey", data);
                        Assert.DoesNotContain("somevalue", data);
                        Assert.DoesNotContain("chunkextension", data);
                    }
                }
            });
        }

        [Theory]
        [InlineData("")] // missing size
        [InlineData("    ")] // missing size
        [InlineData("10000000000000000")] // overflowing size
        [InlineData("xyz")] // non-hex
        [InlineData("7gibberish")] // valid size then gibberish
        [InlineData("7\v\f")] // unacceptable whitespace
        public async Task GetAsync_InvalidChunkSize_ThrowsHttpRequestException(string chunkSize)
        {
            await LoopbackServer.CreateServerAsync(async (server, url) =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    string partialResponse = "HTTP/1.1 200 OK\r\n" +
                        "Transfer-Encoding: chunked\r\n" +
                        "\r\n" +
                        $"{chunkSize}\r\n";

                    var tcs = new TaskCompletionSource<bool>();
                    Task serverTask = server.AcceptConnectionAsync(async connection =>
                        {
                            await connection.ReadRequestHeaderAndSendCustomResponseAsync(partialResponse);
                            await tcs.Task;
                        });

                    await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));
                    tcs.SetResult(true);
                    await serverTask;
                }
            });
        }

        [Fact]
        public async Task GetAsync_InvalidChunkTerminator_ThrowsHttpRequestException()
        {
            await LoopbackServer.CreateClientAndServerAsync(async url =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));
                }
            }, server => server.AcceptConnectionSendCustomResponseAndCloseAsync(
                "HTTP/1.1 200 OK\r\n" +
                "Connection: close\r\n" +
                "Transfer-Encoding: chunked\r\n" +
                "\r\n" +
                "5\r\n" +
                "hello" + // missing \r\n terminator
                            //"5\r\n" +
                            //"world" + // missing \r\n terminator
                "0\r\n" +
                "\r\n"));
        }

        [Fact]
        public async Task GetAsync_InfiniteChunkSize_ThrowsHttpRequestException()
        {
            await LoopbackServer.CreateServerAsync(async (server, url) =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    client.Timeout = Timeout.InfiniteTimeSpan;

                    var cts = new CancellationTokenSource();
                    var tcs = new TaskCompletionSource<bool>();
                    Task serverTask = server.AcceptConnectionAsync(async connection =>
                    {
                        await connection.ReadRequestHeaderAndSendCustomResponseAsync("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n");
                        TextWriter writer = connection.Writer;
                        try
                        {
                            while (!cts.IsCancellationRequested) // infinite to make sure implementation doesn't OOM
                            {
                                await writer.WriteAsync(new string(' ', 10000));
                                await Task.Delay(1);
                            }
                        }
                        catch { }
                    });

                    await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));
                    cts.Cancel();
                    await serverTask;
                }
            });
        }

        [Fact]
        public async Task SendAsync_TransferEncodingSetButNoRequestContent_Throws()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "http://bing.com") { Version = UseVersion };
            req.Headers.TransferEncodingChunked = true;
            using (HttpClient c = CreateHttpClient())
            {
                HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => c.SendAsync(TestAsync, req));
                Assert.IsType<InvalidOperationException>(error.InnerException);
            }
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task GetAsync_ResponseHeadersRead_ReadFromEachIterativelyDoesntDeadlock(Configuration.Http.RemoteServer remoteServer)
        {
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            {
                const int NumGets = 5;
                Task<HttpResponseMessage>[] responseTasks = (from _ in Enumerable.Range(0, NumGets)
                                                             select client.GetAsync(remoteServer.EchoUri, HttpCompletionOption.ResponseHeadersRead)).ToArray();
                for (int i = responseTasks.Length - 1; i >= 0; i--) // read backwards to increase likelihood that we wait on a different task than has data available
                {
                    using (HttpResponseMessage response = await responseTasks[i])
                    {
                        string responseContent = await response.Content.ReadAsStringAsync();
                        _output.WriteLine(responseContent);
                        TestHelper.VerifyResponseBody(
                            responseContent,
                            response.Content.Headers.ContentMD5,
                            false,
                            null);
                    }
                }
            }
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task SendAsync_HttpRequestMsgResponseHeadersRead_StatusCodeOK(Configuration.Http.RemoteServer remoteServer)
        {
            // Sync API supported only up to HTTP/1.1
            if (!TestAsync && remoteServer.HttpVersion.Major >= 2)
            {
                return;
            }

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, remoteServer.EchoUri) { Version = remoteServer.HttpVersion };
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            {
                using (HttpResponseMessage response = await client.SendAsync(TestAsync, request, HttpCompletionOption.ResponseHeadersRead))
                {
                    string responseContent = await response.Content.ReadAsStringAsync();
                    _output.WriteLine(responseContent);
                    TestHelper.VerifyResponseBody(
                        responseContent,
                        response.Content.Headers.ContentMD5,
                        false,
                        null);
                }
            }
        }

        [OuterLoop("Slow response")]
        [Fact]
        public async Task SendAsync_ReadFromSlowStreamingServer_PartialDataReturned()
        {
            await LoopbackServer.CreateServerAsync(async (server, url) =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    Task<HttpResponseMessage> getResponse = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                    await server.AcceptConnectionAsync(async connection =>
                    {
                        await connection.ReadRequestHeaderAndSendCustomResponseAsync(
                            "HTTP/1.1 200 OK\r\n" +
                            $"Date: {DateTimeOffset.UtcNow:R}\r\n" +
                            "Content-Length: 16000\r\n" +
                            "\r\n" +
                            "less than 16000 bytes");

                        using (HttpResponseMessage response = await getResponse)
                        {
                            var buffer = new byte[8000];
                            using (Stream clientStream = await response.Content.ReadAsStreamAsync(TestAsync))
                            {
                                int bytesRead = await clientStream.ReadAsync(buffer, 0, buffer.Length);
                                _output.WriteLine($"Bytes read from stream: {bytesRead}");
                                Assert.True(bytesRead < buffer.Length, "bytesRead should be less than buffer.Length");
                            }
                        }
                    });
                }
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        [InlineData(null)]
        public async Task ReadAsStreamAsync_HandlerProducesWellBehavedResponseStream(bool? chunked)
        {
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            if (LoopbackServerFactory.Version >= HttpVersion20.Value && chunked == true)
            {
                // Chunking is not supported on HTTP/2 and later.
                return;
            }

            await LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = UseVersion };
                using (var client = new HttpMessageInvoker(CreateHttpClientHandler()))
                using (HttpResponseMessage response = await client.SendAsync(TestAsync, request, CancellationToken.None))
                {
                    using (Stream responseStream = await response.Content.ReadAsStreamAsync(TestAsync))
                    {
                        Assert.Same(responseStream, await response.Content.ReadAsStreamAsync(TestAsync));

                        // Boolean properties returning correct values
                        Assert.True(responseStream.CanRead);
                        Assert.False(responseStream.CanWrite);
                        Assert.False(responseStream.CanSeek);

                        // Not supported operations
                        Assert.Throws<NotSupportedException>(() => responseStream.BeginWrite(new byte[1], 0, 1, null, null));
                        Assert.Throws<NotSupportedException>(() => responseStream.Length);
                        Assert.Throws<NotSupportedException>(() => responseStream.Position);
                        Assert.Throws<NotSupportedException>(() => responseStream.Position = 0);
                        Assert.Throws<NotSupportedException>(() => responseStream.Seek(0, SeekOrigin.Begin));
                        Assert.Throws<NotSupportedException>(() => responseStream.SetLength(0));
                        Assert.Throws<NotSupportedException>(() => responseStream.Write(new byte[1], 0, 1));
#if !NETFRAMEWORK
                        Assert.Throws<NotSupportedException>(() => responseStream.Write(new Span<byte>(new byte[1])));
                        Assert.Throws<NotSupportedException>(() => { responseStream.WriteAsync(new Memory<byte>(new byte[1])); });
#endif
                        Assert.Throws<NotSupportedException>(() => { responseStream.WriteAsync(new byte[1], 0, 1); });
                        Assert.Throws<NotSupportedException>(() => responseStream.WriteByte(1));

                        // Invalid arguments
                        var nonWritableStream = new MemoryStream(new byte[1], false);
                        var disposedStream = new MemoryStream();
                        disposedStream.Dispose();
                        Assert.Throws<ArgumentNullException>(() => responseStream.CopyTo(null));
                        Assert.Throws<ArgumentOutOfRangeException>(() => responseStream.CopyTo(Stream.Null, 0));
                        Assert.Throws<ArgumentNullException>(() => { responseStream.CopyToAsync(null, 100, default); });
                        Assert.Throws<ArgumentOutOfRangeException>(() => { responseStream.CopyToAsync(Stream.Null, 0, default); });
                        Assert.Throws<ArgumentOutOfRangeException>(() => { responseStream.CopyToAsync(Stream.Null, -1, default); });
                        Assert.Throws<NotSupportedException>(() => { responseStream.CopyToAsync(nonWritableStream, 100, default); });
                        Assert.Throws<ObjectDisposedException>(() => { responseStream.CopyToAsync(disposedStream, 100, default); });
                        Assert.Throws<ArgumentNullException>(() => responseStream.Read(null, 0, 100));
                        Assert.Throws<ArgumentOutOfRangeException>(() => responseStream.Read(new byte[1], -1, 1));
                        Assert.ThrowsAny<ArgumentException>(() => responseStream.Read(new byte[1], 2, 1));
                        Assert.Throws<ArgumentOutOfRangeException>(() => responseStream.Read(new byte[1], 0, -1));
                        Assert.ThrowsAny<ArgumentException>(() => responseStream.Read(new byte[1], 0, 2));
                        Assert.Throws<ArgumentNullException>(() => responseStream.BeginRead(null, 0, 100, null, null));
                        Assert.Throws<ArgumentOutOfRangeException>(() => responseStream.BeginRead(new byte[1], -1, 1, null, null));
                        Assert.ThrowsAny<ArgumentException>(() => responseStream.BeginRead(new byte[1], 2, 1, null, null));
                        Assert.Throws<ArgumentOutOfRangeException>(() => responseStream.BeginRead(new byte[1], 0, -1, null, null));
                        Assert.ThrowsAny<ArgumentException>(() => responseStream.BeginRead(new byte[1], 0, 2, null, null));
                        Assert.Throws<ArgumentNullException>(() => responseStream.EndRead(null));
                        Assert.Throws<ArgumentNullException>(() => { responseStream.ReadAsync(null, 0, 100, default); });
                        Assert.Throws<ArgumentOutOfRangeException>(() => { responseStream.ReadAsync(new byte[1], -1, 1, default); });
                        Assert.ThrowsAny<ArgumentException>(() => { responseStream.ReadAsync(new byte[1], 2, 1, default); });
                        Assert.Throws<ArgumentOutOfRangeException>(() => { responseStream.ReadAsync(new byte[1], 0, -1, default); });
                        Assert.ThrowsAny<ArgumentException>(() => { responseStream.ReadAsync(new byte[1], 0, 2, default); });

                        // Various forms of reading
                        var buffer = new byte[1];

                        Assert.Equal('h', responseStream.ReadByte());

                        Assert.Equal(1, await Task.Factory.FromAsync(responseStream.BeginRead, responseStream.EndRead, buffer, 0, 1, null));
                        Assert.Equal((byte)'e', buffer[0]);

#if !NETFRAMEWORK
                        Assert.Equal(1, await responseStream.ReadAsync(new Memory<byte>(buffer)));
#else
                        Assert.Equal(1, await responseStream.ReadAsync(buffer, 0, 1));
#endif
                        Assert.Equal((byte)'l', buffer[0]);

                        Assert.Equal(1, await responseStream.ReadAsync(buffer, 0, 1));
                        Assert.Equal((byte)'l', buffer[0]);

#if !NETFRAMEWORK
                        Assert.Equal(1, responseStream.Read(new Span<byte>(buffer)));
#else
                        Assert.Equal(1, await responseStream.ReadAsync(buffer, 0, 1));
#endif
                        Assert.Equal((byte)'o', buffer[0]);

                        Assert.Equal(1, responseStream.Read(buffer, 0, 1));
                        Assert.Equal((byte)' ', buffer[0]);

                        // Doing any of these 0-byte reads causes the connection to fail.
                        Assert.Equal(0, await Task.Factory.FromAsync(responseStream.BeginRead, responseStream.EndRead, Array.Empty<byte>(), 0, 0, null));
#if !NETFRAMEWORK
                        Assert.Equal(0, await responseStream.ReadAsync(Memory<byte>.Empty));
#endif
                        Assert.Equal(0, await responseStream.ReadAsync(Array.Empty<byte>(), 0, 0));
#if !NETFRAMEWORK
                        Assert.Equal(0, responseStream.Read(Span<byte>.Empty));
#endif
                        Assert.Equal(0, responseStream.Read(Array.Empty<byte>(), 0, 0));

                        // And copying
                        var ms = new MemoryStream();
                        await responseStream.CopyToAsync(ms);
                        Assert.Equal("world", Encoding.ASCII.GetString(ms.ToArray()));

                        // Read and copy again once we've exhausted all data
                        ms = new MemoryStream();
                        await responseStream.CopyToAsync(ms);
                        responseStream.CopyTo(ms);
                        Assert.Equal(0, ms.Length);
                        Assert.Equal(-1, responseStream.ReadByte());
                        Assert.Equal(0, responseStream.Read(buffer, 0, 1));
#if !NETFRAMEWORK
                        Assert.Equal(0, responseStream.Read(new Span<byte>(buffer)));
#endif
                        Assert.Equal(0, await responseStream.ReadAsync(buffer, 0, 1));
#if !NETFRAMEWORK
                        Assert.Equal(0, await responseStream.ReadAsync(new Memory<byte>(buffer)));
#endif
                        Assert.Equal(0, await Task.Factory.FromAsync(responseStream.BeginRead, responseStream.EndRead, buffer, 0, 1, null));
                    }
                }
            }, async server =>
            {
                await server.AcceptConnectionAsync(async connection =>
                {
                    await connection.ReadRequestDataAsync();
                    switch (chunked)
                    {
                        case true:
                            await connection.SendResponseAsync(HttpStatusCode.OK, headers: new HttpHeaderData[] { new HttpHeaderData("Transfer-Encoding", "chunked") }, isFinal: false);
                            await connection.SendResponseBodyAsync("3\r\nhel\r\n8\r\nlo world\r\n0\r\n\r\n");
                            break;

                        case false:
                            await connection.SendResponseAsync(HttpStatusCode.OK, headers: new HttpHeaderData[] { new HttpHeaderData("Content-Length", "11")}, content: "hello world");
                            break;

                        case null:
                            // This inject Content-Length header with null value to hint Loopback code to not include one automatically.
                            await connection.SendResponseAsync(HttpStatusCode.OK, headers: new HttpHeaderData[] { new HttpHeaderData("Content-Length", null)}, isFinal: false);
                            await connection.SendResponseBodyAsync("hello world");
                            break;
                    }
                });
            });
        }

        [Fact]
        public async Task ReadAsStreamAsync_EmptyResponseBody_HandlerProducesWellBehavedResponseStream()
        {
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            await LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using (var client = new HttpMessageInvoker(CreateHttpClientHandler()))
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = UseVersion };

                    using (HttpResponseMessage response = await client.SendAsync(TestAsync, request, CancellationToken.None))
                    using (Stream responseStream = await response.Content.ReadAsStreamAsync(TestAsync))
                    {
                        // Boolean properties returning correct values
                        Assert.True(responseStream.CanRead);
                        Assert.False(responseStream.CanWrite);
                        Assert.False(responseStream.CanSeek);

                        // Not supported operations
                        Assert.Throws<NotSupportedException>(() => responseStream.BeginWrite(new byte[1], 0, 1, null, null));
                        Assert.Throws<NotSupportedException>(() => responseStream.Length);
                        Assert.Throws<NotSupportedException>(() => responseStream.Position);
                        Assert.Throws<NotSupportedException>(() => responseStream.Position = 0);
                        Assert.Throws<NotSupportedException>(() => responseStream.Seek(0, SeekOrigin.Begin));
                        Assert.Throws<NotSupportedException>(() => responseStream.SetLength(0));
                        Assert.Throws<NotSupportedException>(() => responseStream.Write(new byte[1], 0, 1));
#if !NETFRAMEWORK
                        Assert.Throws<NotSupportedException>(() => responseStream.Write(new Span<byte>(new byte[1])));
                        await Assert.ThrowsAsync<NotSupportedException>(async () => await responseStream.WriteAsync(new Memory<byte>(new byte[1])));
#endif
                        await Assert.ThrowsAsync<NotSupportedException>(async () => await responseStream.WriteAsync(new byte[1], 0, 1));
                        Assert.Throws<NotSupportedException>(() => responseStream.WriteByte(1));

                        // Invalid arguments
                        var nonWritableStream = new MemoryStream(new byte[1], false);
                        var disposedStream = new MemoryStream();
                        disposedStream.Dispose();
                        Assert.Throws<ArgumentNullException>(() => responseStream.CopyTo(null));
                        Assert.Throws<ArgumentOutOfRangeException>(() => responseStream.CopyTo(Stream.Null, 0));
                        Assert.Throws<ArgumentNullException>(() => { responseStream.CopyToAsync(null, 100, default); });
                        Assert.Throws<ArgumentOutOfRangeException>(() => { responseStream.CopyToAsync(Stream.Null, 0, default); });
                        Assert.Throws<ArgumentOutOfRangeException>(() => { responseStream.CopyToAsync(Stream.Null, -1, default); });
                        Assert.Throws<NotSupportedException>(() => { responseStream.CopyToAsync(nonWritableStream, 100, default); });
                        Assert.Throws<ObjectDisposedException>(() => { responseStream.CopyToAsync(disposedStream, 100, default); });
                        Assert.Throws<ArgumentNullException>(() => responseStream.Read(null, 0, 100));
                        Assert.Throws<ArgumentOutOfRangeException>(() => responseStream.Read(new byte[1], -1, 1));
                        Assert.ThrowsAny<ArgumentException>(() => responseStream.Read(new byte[1], 2, 1));
                        Assert.Throws<ArgumentOutOfRangeException>(() => responseStream.Read(new byte[1], 0, -1));
                        Assert.ThrowsAny<ArgumentException>(() => responseStream.Read(new byte[1], 0, 2));
                        Assert.Throws<ArgumentNullException>(() => responseStream.BeginRead(null, 0, 100, null, null));
                        Assert.Throws<ArgumentOutOfRangeException>(() => responseStream.BeginRead(new byte[1], -1, 1, null, null));
                        Assert.ThrowsAny<ArgumentException>(() => responseStream.BeginRead(new byte[1], 2, 1, null, null));
                        Assert.Throws<ArgumentOutOfRangeException>(() => responseStream.BeginRead(new byte[1], 0, -1, null, null));
                        Assert.ThrowsAny<ArgumentException>(() => responseStream.BeginRead(new byte[1], 0, 2, null, null));
                        Assert.Throws<ArgumentNullException>(() => responseStream.EndRead(null));
                        Assert.Throws<ArgumentNullException>(() => { responseStream.CopyTo(null); });
                        Assert.Throws<ArgumentNullException>(() => { responseStream.CopyToAsync(null, 100, default); });
                        Assert.Throws<ArgumentNullException>(() => { responseStream.CopyToAsync(null, 100, default); });
                        Assert.Throws<ArgumentNullException>(() => { responseStream.Read(null, 0, 100); });
                        Assert.Throws<ArgumentNullException>(() => { responseStream.ReadAsync(null, 0, 100, default); });
                        Assert.Throws<ArgumentNullException>(() => { responseStream.BeginRead(null, 0, 100, null, null); });

                        // Empty reads
                        var buffer = new byte[1];
                        Assert.Equal(-1, responseStream.ReadByte());
                        Assert.Equal(0, await Task.Factory.FromAsync(responseStream.BeginRead, responseStream.EndRead, buffer, 0, 1, null));
#if !NETFRAMEWORK
                        Assert.Equal(0, await responseStream.ReadAsync(new Memory<byte>(buffer)));
#endif
                        Assert.Equal(0, await responseStream.ReadAsync(buffer, 0, 1));
#if !NETFRAMEWORK
                        Assert.Equal(0, responseStream.Read(new Span<byte>(buffer)));
#endif
                        Assert.Equal(0, responseStream.Read(buffer, 0, 1));

                        // Empty copies
                        var ms = new MemoryStream();
                        await responseStream.CopyToAsync(ms);
                        Assert.Equal(0, ms.Length);
                        responseStream.CopyTo(ms);
                        Assert.Equal(0, ms.Length);
                    }
                }
            },
            server => server.AcceptConnectionSendResponseAndCloseAsync());
        }

        [Fact]
        public async Task Dispose_DisposingHandlerCancelsActiveOperationsWithoutResponses()
        {
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            await LoopbackServerFactory.CreateServerAsync(async (server1, url1) =>
            {
                await LoopbackServerFactory.CreateServerAsync(async (server2, url2) =>
                {
                    await LoopbackServerFactory.CreateServerAsync(async (server3, url3) =>
                    {
                        var unblockServers = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                        // First server connects but doesn't send any response yet
                        Task serverTask1 = server1.AcceptConnectionAsync(async connection1 =>
                        {
                            await unblockServers.Task;
                        });

                        // Second server connects and sends some but not all headers
                        Task serverTask2 = server2.AcceptConnectionAsync(async connection2 =>
                        {
                            await connection2.ReadRequestDataAsync();
                            await connection2.SendResponseAsync(HttpStatusCode.OK, content: null, isFinal : false);
                            await unblockServers.Task;
                        });

                        // Third server connects and sends all headers and some but not all of the body
                        Task serverTask3 = server3.AcceptConnectionAsync(async connection3 =>
                        {
                            await connection3.ReadRequestDataAsync();
                            await connection3.SendResponseAsync(HttpStatusCode.OK, new HttpHeaderData[] { new HttpHeaderData("Content-Length", "20") }, isFinal : false);
                            await connection3.SendResponseBodyAsync("1234567890", isFinal : false);
                            await unblockServers.Task;
                            await connection3.SendResponseBodyAsync("1234567890", isFinal : true);
                        });

                        // Make three requests
                        Task<HttpResponseMessage> get1, get2;
                        HttpResponseMessage response3;
                        using (HttpClient client = CreateHttpClient())
                        {
                            get1 = client.GetAsync(url1, HttpCompletionOption.ResponseHeadersRead);
                            get2 = client.GetAsync(url2, HttpCompletionOption.ResponseHeadersRead);
                            response3 = await client.GetAsync(url3, HttpCompletionOption.ResponseHeadersRead);
                        } // Dispose the handler while requests are still outstanding

                        // Requests 1 and 2 should be canceled as we haven't finished receiving their headers
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => get1);
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => get2);

                        // Request 3 should still be active, and we should be able to receive all of the data.
                        unblockServers.SetResult(true);
                        using (response3)
                        {
                            Assert.Equal("12345678901234567890", await response3.Content.ReadAsStringAsync());
                        }
                    });
                });
            });
        }

        [Theory]
        [InlineData(99)]
        [InlineData(1000)]
        public async Task GetAsync_StatusCodeOutOfRange_ExpectedException(int statusCode)
        {
            await LoopbackServer.CreateServerAsync(async (server, url) =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    Task<HttpResponseMessage> getResponseTask = client.GetAsync(url);
                    await server.AcceptConnectionSendCustomResponseAndCloseAsync(
                            $"HTTP/1.1 {statusCode}\r\n" +
                            $"Date: {DateTimeOffset.UtcNow:R}\r\n" +
                            "Connection: close\r\n" +
                            "\r\n");

                    await Assert.ThrowsAsync<HttpRequestException>(() => getResponseTask);
                }
            });
        }

        [OuterLoop("Uses external server")]
        [Fact]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/29424")]
        public async Task GetAsync_UnicodeHostName_SuccessStatusCodeInResponse()
        {
            using (HttpClient client = CreateHttpClient())
            {
                // international version of the Starbucks website
                // punycode: xn--oy2b35ckwhba574atvuzkc.com
                string server = "http://\uc2a4\ud0c0\ubc85\uc2a4\ucf54\ub9ac\uc544.com";
                using (HttpResponseMessage response = await client.GetAsync(server))
                {
                    response.EnsureSuccessStatusCode();
                }
            }
        }

#region Post Methods Tests

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task PostAsync_CallMethodTwice_StringContent(Configuration.Http.RemoteServer remoteServer)
        {
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            {
                string data = "Test String";
                var content = new StringContent(data, Encoding.UTF8);
                content.Headers.ContentMD5 = TestHelper.ComputeMD5Hash(data);
                HttpResponseMessage response;
                using (response = await client.PostAsync(remoteServer.VerifyUploadUri, content))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }

                // Repeat call.
                content = new StringContent(data, Encoding.UTF8);
                content.Headers.ContentMD5 = TestHelper.ComputeMD5Hash(data);
                using (response = await client.PostAsync(remoteServer.VerifyUploadUri, content))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }
            }
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task PostAsync_CallMethod_UnicodeStringContent(Configuration.Http.RemoteServer remoteServer)
        {
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            {
                string data = "\ub4f1\uffc7\u4e82\u67ab4\uc6d4\ud1a0\uc694\uc77c\uffda3\u3155\uc218\uffdb";
                var content = new StringContent(data, Encoding.UTF8);
                content.Headers.ContentMD5 = TestHelper.ComputeMD5Hash(data);

                using (HttpResponseMessage response = await client.PostAsync(remoteServer.VerifyUploadUri, content))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }
            }
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(VerifyUploadServersStreamsAndExpectedData))]
        public async Task PostAsync_CallMethod_StreamContent(Configuration.Http.RemoteServer remoteServer, HttpContent content, byte[] expectedData)
        {
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            {
                content.Headers.ContentMD5 = TestHelper.ComputeMD5Hash(expectedData);

                using (HttpResponseMessage response = await client.PostAsync(remoteServer.VerifyUploadUri, content))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }
            }
        }

        private sealed class StreamContentWithSyncAsyncCopy : StreamContent
        {
            private readonly Stream _stream;
            private readonly bool _syncCopy;

            public StreamContentWithSyncAsyncCopy(Stream stream, bool syncCopy) : base(stream)
            {
                _stream = stream;
                _syncCopy = syncCopy;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                if (_syncCopy)
                {
                    try
                    {
                        _stream.CopyTo(stream, 128); // arbitrary size likely to require multiple read/writes
                        return Task.CompletedTask;
                    }
                    catch (Exception exc)
                    {
                        return Task.FromException(exc);
                    }
                }

                return base.SerializeToStreamAsync(stream, context);
            }
        }

        public static IEnumerable<object[]> VerifyUploadServersStreamsAndExpectedData
        {
            get
            {
                foreach (Configuration.Http.RemoteServer remoteServer in Configuration.Http.RemoteServers) // target server
                    foreach (bool syncCopy in BoolValues) // force the content copy to happen via Read/Write or ReadAsync/WriteAsync
                    {
                        byte[] data = new byte[1234];
                        new Random(42).NextBytes(data);

                        // A MemoryStream
                        {
                            var memStream = new MemoryStream(data, writable: false);
                            yield return new object[] { remoteServer, new StreamContentWithSyncAsyncCopy(memStream, syncCopy: syncCopy), data };
                        }

                        // A multipart content that provides its own stream from CreateContentReadStreamAsync
                        {
                            var mc = new MultipartContent();
                            mc.Add(new ByteArrayContent(data));
                            var memStream = new MemoryStream();
                            mc.CopyToAsync(memStream).GetAwaiter().GetResult();
                            yield return new object[] { remoteServer, mc, memStream.ToArray() };
                        }

                        // A stream that provides the data synchronously and has a known length
                        {
                            var wrappedMemStream = new MemoryStream(data, writable: false);
                            var syncKnownLengthStream = new DelegateStream(
                                canReadFunc: () => wrappedMemStream.CanRead,
                                canSeekFunc: () => wrappedMemStream.CanSeek,
                                lengthFunc: () => wrappedMemStream.Length,
                                positionGetFunc: () => wrappedMemStream.Position,
                                positionSetFunc: p => wrappedMemStream.Position = p,
                                readFunc: (buffer, offset, count) => wrappedMemStream.Read(buffer, offset, count),
                                readAsyncFunc: (buffer, offset, count, token) => wrappedMemStream.ReadAsync(buffer, offset, count, token));
                            yield return new object[] { remoteServer, new StreamContentWithSyncAsyncCopy(syncKnownLengthStream, syncCopy: syncCopy), data };
                        }

                        // A stream that provides the data synchronously and has an unknown length
                        {
                            int syncUnknownLengthStreamOffset = 0;

                            Func<byte[], int, int, int> readFunc = (buffer, offset, count) =>
                            {
                                int bytesRemaining = data.Length - syncUnknownLengthStreamOffset;
                                int bytesToCopy = Math.Min(bytesRemaining, count);
                                Array.Copy(data, syncUnknownLengthStreamOffset, buffer, offset, bytesToCopy);
                                syncUnknownLengthStreamOffset += bytesToCopy;
                                return bytesToCopy;
                            };

                            var syncUnknownLengthStream = new DelegateStream(
                                canReadFunc: () => true,
                                canSeekFunc: () => false,
                                readFunc: readFunc,
                                readAsyncFunc: (buffer, offset, count, token) => Task.FromResult(readFunc(buffer, offset, count)));
                            yield return new object[] { remoteServer, new StreamContentWithSyncAsyncCopy(syncUnknownLengthStream, syncCopy: syncCopy), data };
                        }

                        // A stream that provides the data asynchronously
                        {
                            int asyncStreamOffset = 0, maxDataPerRead = 100;

                            Func<byte[], int, int, int> readFunc = (buffer, offset, count) =>
                            {
                                int bytesRemaining = data.Length - asyncStreamOffset;
                                int bytesToCopy = Math.Min(bytesRemaining, Math.Min(maxDataPerRead, count));
                                Array.Copy(data, asyncStreamOffset, buffer, offset, bytesToCopy);
                                asyncStreamOffset += bytesToCopy;
                                return bytesToCopy;
                            };

                            var asyncStream = new DelegateStream(
                                canReadFunc: () => true,
                                canSeekFunc: () => false,
                                readFunc: readFunc,
                                readAsyncFunc: async (buffer, offset, count, token) =>
                                {
                                    await Task.Delay(1).ConfigureAwait(false);
                                    return readFunc(buffer, offset, count);
                                });
                            yield return new object[] { remoteServer, new StreamContentWithSyncAsyncCopy(asyncStream, syncCopy: syncCopy), data };
                        }

                        // Providing data from a FormUrlEncodedContent's stream
                        {
                            var formContent = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("key", "val") });
                            yield return new object[] { remoteServer, formContent, Encoding.GetEncoding("iso-8859-1").GetBytes("key=val") };
                        }
                    }
            }
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task PostAsync_CallMethod_NullContent(Configuration.Http.RemoteServer remoteServer)
        {
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            {
                using (HttpResponseMessage response = await client.PostAsync(remoteServer.EchoUri, null))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                    string responseContent = await response.Content.ReadAsStringAsync();
                    _output.WriteLine(responseContent);
                    TestHelper.VerifyResponseBody(
                        responseContent,
                        response.Content.Headers.ContentMD5,
                        false,
                        string.Empty);
                }
            }
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task PostAsync_CallMethod_EmptyContent(Configuration.Http.RemoteServer remoteServer)
        {
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            {
                var content = new StringContent(string.Empty);
                using (HttpResponseMessage response = await client.PostAsync(remoteServer.EchoUri, content))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                    string responseContent = await response.Content.ReadAsStringAsync();
                    _output.WriteLine(responseContent);
                    TestHelper.VerifyResponseBody(
                        responseContent,
                        response.Content.Headers.ContentMD5,
                        false,
                        string.Empty);
                }
            }
        }

        public static IEnumerable<object[]> ExpectContinueVersion()
        {
            return
                from expect in new bool?[] {true, false, null}
                from version in new Version[] {new Version(1, 0), new Version(1, 1), new Version(2, 0)}
                select new object[] {expect, version};
        }

        [OuterLoop("Uses external server")]
        [Theory]
        [MemberData(nameof(ExpectContinueVersion))]
        public async Task PostAsync_ExpectContinue_Success(bool? expectContinue, Version version)
        {
            // Sync API supported only up to HTTP/1.1
            if (!TestAsync && version.Major >= 2)
            {
                return;
            }

            using (HttpClient client = CreateHttpClient())
            {
                var req = new HttpRequestMessage(HttpMethod.Post, version.Major == 2 ? Configuration.Http.Http2RemoteEchoServer : Configuration.Http.RemoteEchoServer)
                {
                    Content = new StringContent("Test String", Encoding.UTF8),
                    Version = version
                };
                req.Headers.ExpectContinue = expectContinue;

                using (HttpResponseMessage response = await client.SendAsync(TestAsync, req))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                    if (!IsWinHttpHandler)
                    {
                        const string ExpectedReqHeader = "\"Expect\": \"100-continue\"";
                        if (expectContinue == true && (version >= new Version(1, 1)))
                        {
                            Assert.Contains(ExpectedReqHeader, await response.Content.ReadAsStringAsync());
                        }
                        else
                        {
                            Assert.DoesNotContain(ExpectedReqHeader, await response.Content.ReadAsStringAsync());
                        }
                    }
                }
            }
        }

        [Fact]
        public async Task GetAsync_ExpectContinueTrue_NoContent_StillSendsHeader()
        {
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            const string ExpectedContent = "Hello, expecting and continuing world.";
            var clientCompleted = new TaskCompletionSource<bool>();
            await LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    client.DefaultRequestHeaders.ExpectContinue = true;
                    Assert.Equal(ExpectedContent, await client.GetStringAsync(uri));
                    clientCompleted.SetResult(true);
                }
            }, async server =>
            {
                await server.AcceptConnectionAsync(async connection =>
                {
                    HttpRequestData requestData = await connection.ReadRequestDataAsync();
                    Assert.Equal("100-continue", requestData.GetSingleHeaderValue("Expect"));

                    await connection.SendResponseAsync(HttpStatusCode.Continue, isFinal: false);
                    await connection.SendResponseAsync(HttpStatusCode.OK, new HttpHeaderData[] { new HttpHeaderData("Content-Length", ExpectedContent.Length.ToString()) }, isFinal: false);
                    await connection.SendResponseBodyAsync(ExpectedContent);
                    await clientCompleted.Task; // make sure server closing the connection isn't what let the client complete
                });
            });
        }

        public static IEnumerable<object[]> Interim1xxStatusCode()
        {
            yield return new object[] { (HttpStatusCode) 100 }; // 100 Continue.
            // 101 SwitchingProtocols will be treated as a final status code.
            yield return new object[] { (HttpStatusCode) 102 }; // 102 Processing.
            yield return new object[] { (HttpStatusCode) 103 }; // 103 EarlyHints.
            yield return new object[] { (HttpStatusCode) 150 };
            yield return new object[] { (HttpStatusCode) 180 };
            yield return new object[] { (HttpStatusCode) 199 };
        }

        [Theory]
        [MemberData(nameof(Interim1xxStatusCode))]
        public async Task SendAsync_1xxResponsesWithHeaders_InterimResponsesHeadersIgnored(HttpStatusCode responseStatusCode)
        {
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            var clientFinished = new TaskCompletionSource<bool>();
            const string TestString = "test";
            const string CookieHeaderExpected = "yummy_cookie=choco";
            const string SetCookieExpected = "theme=light";
            const string ContentTypeHeaderExpected = "text/html";

            const string SetCookieIgnored1 = "hello=world";
            const string SetCookieIgnored2 = "net=core";

            await LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using (var handler = CreateHttpClientHandler())
                using (HttpClient client = CreateHttpClient(handler))
                {
                    HttpRequestMessage initialMessage = new HttpRequestMessage(HttpMethod.Post, uri) { Version = UseVersion };
                    initialMessage.Content = new StringContent(TestString);
                    initialMessage.Headers.ExpectContinue = true;
                    HttpResponseMessage response = await client.SendAsync(TestAsync, initialMessage);

                    // Verify status code.
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    // Verify Cookie header.
                    Assert.Equal(1, response.Headers.GetValues("Cookie").Count());
                    Assert.Equal(CookieHeaderExpected, response.Headers.GetValues("Cookie").First().ToString());
                    // Verify Set-Cookie header.
                    Assert.Equal(1, handler.CookieContainer.Count);
                    Assert.Equal(SetCookieExpected, handler.CookieContainer.GetCookieHeader(uri));
                    // Verify Content-type header.
                    Assert.Equal(ContentTypeHeaderExpected, response.Content.Headers.ContentType.ToString());
                    clientFinished.SetResult(true);
                }
            }, async server =>
            {
                await server.AcceptConnectionAsync(async connection =>
                {
                    // Send 100-Continue responses with additional headers.
                    HttpRequestData requestData = await connection.ReadRequestDataAsync(readBody: true);
                    await connection.SendResponseAsync(responseStatusCode, headers: new HttpHeaderData[] {
                            new HttpHeaderData("Cookie", "ignore_cookie=choco1"),
                            new HttpHeaderData("Content-type", "text/xml"),
                            new HttpHeaderData("Set-Cookie", SetCookieIgnored1)}, isFinal: false);

                    await connection.SendResponseAsync(responseStatusCode, headers:  new HttpHeaderData[] {
                        new HttpHeaderData("Cookie", "ignore_cookie=choco2"),
                        new HttpHeaderData("Content-type", "text/plain"),
                        new HttpHeaderData("Set-Cookie", SetCookieIgnored2)}, isFinal: false);

                    Assert.Equal(TestString, Encoding.ASCII.GetString(requestData.Body));

                    // Send final status code.
                    await connection.SendResponseAsync(HttpStatusCode.OK, headers: new HttpHeaderData[] {
                        new HttpHeaderData("Cookie", CookieHeaderExpected),
                        new HttpHeaderData("Content-type", ContentTypeHeaderExpected),
                        new HttpHeaderData("Set-Cookie", SetCookieExpected)});

                    await clientFinished.Task;
                });
            });
        }

        [Theory]
        [MemberData(nameof(Interim1xxStatusCode))]
        public async Task SendAsync_Unexpected1xxResponses_DropAllInterimResponses(HttpStatusCode responseStatusCode)
        {
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            var clientFinished = new TaskCompletionSource<bool>();
            const string TestString = "test";

            await LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    HttpRequestMessage initialMessage = new HttpRequestMessage(HttpMethod.Post, uri) { Version = UseVersion };
                    initialMessage.Content = new StringContent(TestString);
                    // No ExpectContinue header.
                    initialMessage.Headers.ExpectContinue = false;
                    HttpResponseMessage response = await client.SendAsync(TestAsync, initialMessage);

                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    clientFinished.SetResult(true);
                }
            }, async server =>
            {
                await server.AcceptConnectionAsync(async connection =>
                {
                    // Send unexpected 1xx responses.
                    HttpRequestData requestData = await connection.ReadRequestDataAsync(readBody: false);
                    await connection.SendResponseAsync(responseStatusCode, isFinal: false);
                    await connection.SendResponseAsync(responseStatusCode, isFinal: false);
                    await connection.SendResponseAsync(responseStatusCode, isFinal: false);

                    byte[] body = await connection.ReadRequestBodyAsync();
                    Assert.Equal(TestString, Encoding.ASCII.GetString(body));

                    // Send final status code.
                    await connection.SendResponseAsync(HttpStatusCode.OK);
                    await clientFinished.Task;
                });
            });
        }

        [Fact]
        public async Task SendAsync_MultipleExpected100Responses_ReceivesCorrectResponse()
        {
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            var clientFinished = new TaskCompletionSource<bool>();
            const string TestString = "test";

            await LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    HttpRequestMessage initialMessage = new HttpRequestMessage(HttpMethod.Post, uri) { Version = UseVersion };
                    initialMessage.Content = new StringContent(TestString);
                    initialMessage.Headers.ExpectContinue = true;
                    HttpResponseMessage response = await client.SendAsync(TestAsync, initialMessage);

                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    clientFinished.SetResult(true);
                }
            }, async server =>
            {
                await server.AcceptConnectionAsync(async connection =>
                {
                    await connection.ReadRequestDataAsync(readBody: false);
                    // Send multiple 100-Continue responses.
                    for (int count = 0 ; count < 4; count++)
                    {
                        await connection.SendResponseAsync(HttpStatusCode.Continue, isFinal: false);
                    }

                    byte[] body = await connection.ReadRequestBodyAsync();
                    Assert.Equal(TestString, Encoding.ASCII.GetString(body));

                    // Send final status code.
                    await connection.SendResponseAsync(HttpStatusCode.OK);
                    await clientFinished.Task;
                });
            });
        }

        [Fact]
        public async Task SendAsync_Expect100Continue_RequestBodyFails_ThrowsContentException()
        {
            if (IsWinHttpHandler)
            {
                return;
            }
            if (!TestAsync && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            var clientFinished = new TaskCompletionSource<bool>();

            await LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    HttpRequestMessage initialMessage = new HttpRequestMessage(HttpMethod.Post, uri) { Version = UseVersion };
                    initialMessage.Content = new ThrowingContent(() => new ThrowingContentException());
                    initialMessage.Headers.ExpectContinue = true;
                    await Assert.ThrowsAsync<ThrowingContentException>(() => client.SendAsync(TestAsync, initialMessage));

                    clientFinished.SetResult(true);
                }
            }, async server =>
            {
                await server.AcceptConnectionAsync(async connection =>
                {
                    try
                    {
                        await connection.ReadRequestDataAsync(readBody: true);
                    }
                    catch { } // Eat errors from client disconnect.
                    await clientFinished.Task.TimeoutAfter(TimeSpan.FromMinutes(2));
                });
            });
        }

        [Fact]
        public async Task SendAsync_No100ContinueReceived_RequestBodySentEventually()
        {
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            var clientFinished = new TaskCompletionSource<bool>();
            const string RequestString = "request";
            const string ResponseString = "response";

            await LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    HttpRequestMessage initialMessage = new HttpRequestMessage(HttpMethod.Post, uri) { Version = UseVersion };
                    initialMessage.Content = new StringContent(RequestString);
                    initialMessage.Headers.ExpectContinue = true;
                    using (HttpResponseMessage response = await client.SendAsync(TestAsync, initialMessage))
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        Assert.Equal(ResponseString, await response.Content.ReadAsStringAsync());
                    }

                    clientFinished.SetResult(true);
                }
            }, async server =>
            {
                await server.AcceptConnectionAsync(async connection =>
                {
                    await connection.ReadRequestDataAsync(readBody: false);

                    await connection.SendResponseAsync(HttpStatusCode.OK, headers: new HttpHeaderData[] {new HttpHeaderData("Content-Length", $"{ResponseString.Length}")}, isFinal : false);

                    byte[] body = await connection.ReadRequestBodyAsync();
                    Assert.Equal(RequestString, Encoding.ASCII.GetString(body));

                    await connection.SendResponseBodyAsync(ResponseString);

                    await clientFinished.Task;
                });
            });
        }

        [Fact]
        public async Task SendAsync_101SwitchingProtocolsResponse_Success()
        {
            // WinHttpHandler and CurlHandler will hang, waiting for additional response.
            // Other handlers will accept 101 as a final response.
            if (IsWinHttpHandler)
            {
                return;
            }

            if (LoopbackServerFactory.Version >= HttpVersion20.Value)
            {
                // Upgrade is not supported on HTTP/2 and later
                return;
            }

            var clientFinished = new TaskCompletionSource<bool>();

            await LoopbackServer.CreateClientAndServerAsync(async uri =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                    Assert.Equal(HttpStatusCode.SwitchingProtocols, response.StatusCode);
                    clientFinished.SetResult(true);
                }
            }, async server =>
            {
                await server.AcceptConnectionAsync(async connection =>
                {
                    // Send a valid 101 Switching Protocols response.
                    await connection.ReadRequestHeaderAndSendCustomResponseAsync(
                        "HTTP/1.1 101 Switching Protocols\r\n" + "Upgrade: websocket\r\n" + "Connection: Upgrade\r\n\r\n");
                    await clientFinished.Task;
                });
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PostAsync_ThrowFromContentCopy_RequestFails(bool syncFailure)
        {
            await LoopbackServer.CreateServerAsync(async (server, uri) =>
            {
                Task responseTask = server.AcceptConnectionAsync(async connection =>
                {
                    var buffer = new byte[1000];
                    while (await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, buffer.Length), SocketFlags.None) != 0);
                });

                using (HttpClient client = CreateHttpClient())
                {
                    Exception error = new FormatException();
                    var content = new StreamContent(new DelegateStream(
                        canSeekFunc: () => true,
                        lengthFunc: () => 12345678,
                        positionGetFunc: () => 0,
                        canReadFunc: () => true,
                        readFunc: (buffer, offset, count) => throw error,
                        readAsyncFunc: (buffer, offset, count, cancellationToken) => syncFailure ? throw error : Task.Delay(1).ContinueWith<int>(_ => throw error)));

                    Assert.Same(error, await Assert.ThrowsAsync<FormatException>(() => client.PostAsync(uri, content)));
                }
            });
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task PostAsync_Redirect_ResultingGetFormattedCorrectly(Configuration.Http.RemoteServer remoteServer)
        {
            const string ContentString = "This is the content string.";
            var content = new StringContent(ContentString);
            Uri redirectUri = remoteServer.RedirectUriForDestinationUri(
                302,
                remoteServer.EchoUri,
                1);

            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            using (HttpResponseMessage response = await client.PostAsync(redirectUri, content))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                string responseContent = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain(ContentString, responseContent);
                Assert.DoesNotContain("Content-Length", responseContent);
            }
        }

        [OuterLoop("Takes several seconds")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task PostAsync_RedirectWith307_LargePayload(Configuration.Http.RemoteServer remoteServer)
        {
            if (remoteServer.HttpVersion == new Version(2, 0))
            {
                // This is occasionally timing out in CI with SocketsHttpHandler and HTTP2, particularly on Linux
                // Likely this is just a very slow test and not a product issue, so just increasing the timeout may be the right fix.
                // Disable until we can investigate further.
                return;
            }

            await PostAsync_Redirect_LargePayload_Helper(remoteServer, 307, true);
        }

        [OuterLoop("Takes several seconds")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task PostAsync_RedirectWith302_LargePayload(Configuration.Http.RemoteServer remoteServer)
        {
            await PostAsync_Redirect_LargePayload_Helper(remoteServer, 302, false);
        }

        public async Task PostAsync_Redirect_LargePayload_Helper(Configuration.Http.RemoteServer remoteServer, int statusCode, bool expectRedirectToPost)
        {
            using (var fs = new FileStream(
                Path.Combine(Path.GetTempPath(), Path.GetTempFileName()),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                0x1000,
                FileOptions.DeleteOnClose))
            {
                string contentString = string.Join("", Enumerable.Repeat("Content", 100000));
                byte[] contentBytes = Encoding.UTF32.GetBytes(contentString);
                fs.Write(contentBytes, 0, contentBytes.Length);
                fs.Flush(flushToDisk: true);
                fs.Position = 0;

                Uri redirectUri = remoteServer.RedirectUriForDestinationUri(
                    statusCode: statusCode,
                    destinationUri: remoteServer.VerifyUploadUri,
                    hops: 1);
                var content = new StreamContent(fs);

                // Compute MD5 of request body data. This will be verified by the server when it receives the request.
                content.Headers.ContentMD5 = TestHelper.ComputeMD5Hash(contentBytes);

                using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
                using (HttpResponseMessage response = await client.PostAsync(redirectUri, content))
                {
                    try
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    }
                    catch
                    {
                        _output.WriteLine($"{(int)response.StatusCode} {response.ReasonPhrase}");
                        throw;
                    }

                    if (expectRedirectToPost)
                    {
                        IEnumerable<string> headerValue = response.Headers.GetValues("X-HttpRequest-Method");
                        Assert.Equal("POST", headerValue.First());
                    }
                }
            }
        }

#if !NETFRAMEWORK
        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(RemoteServersMemberData))]
        public async Task PostAsync_ReuseRequestContent_Success(Configuration.Http.RemoteServer remoteServer)
        {
            const string ContentString = "This is the content string.";
            using (HttpClient client = CreateHttpClientForRemoteServer(remoteServer))
            {
                var content = new StringContent(ContentString);
                for (int i = 0; i < 2; i++)
                {
                    using (HttpResponseMessage response = await client.PostAsync(remoteServer.EchoUri, content))
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        Assert.Contains(ContentString, await response.Content.ReadAsStringAsync());
                    }
                }
            }
        }
#endif

        [Theory]
        [InlineData(HttpStatusCode.MethodNotAllowed, "Custom description")]
        [InlineData(HttpStatusCode.MethodNotAllowed, "")]
        public async Task GetAsync_CallMethod_ExpectedStatusLine(HttpStatusCode statusCode, string reasonPhrase)
        {
            if (LoopbackServerFactory.Version >= HttpVersion20.Value)
            {
                // Custom messages are not supported on HTTP2 and later.
                return;
            }

            await LoopbackServer.CreateClientAndServerAsync(async uri =>
            {
                using (HttpClient client = CreateHttpClient())
                using (HttpResponseMessage response = await client.GetAsync(uri))
                {
                    Assert.Equal(statusCode, response.StatusCode);
                    Assert.Equal(reasonPhrase, response.ReasonPhrase);
                }
            }, server => server.AcceptConnectionSendCustomResponseAndCloseAsync(
                $"HTTP/1.1 {(int)statusCode} {reasonPhrase}\r\nContent-Length: 0\r\n\r\n"));
        }

#endregion

#region Various HTTP Method Tests

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(HttpMethods))]
        public async Task SendAsync_SendRequestUsingMethodToEchoServerWithNoContent_MethodCorrectlySent(
            string method,
            Uri serverUri)
        {
            using (HttpClient client = CreateHttpClient())
            {
                var request = new HttpRequestMessage(
                    new HttpMethod(method),
                    serverUri) { Version = UseVersion };

                using (HttpResponseMessage response = await client.SendAsync(TestAsync, request))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    TestHelper.VerifyRequestMethod(response, method);
                }
            }
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(HttpMethodsThatAllowContent))]
        public async Task SendAsync_SendRequestUsingMethodToEchoServerWithContent_Success(
            string method,
            Uri serverUri)
        {
            using (HttpClient client = CreateHttpClient())
            {
                var request = new HttpRequestMessage(
                    new HttpMethod(method),
                    serverUri) { Version = UseVersion };
                request.Content = new StringContent(ExpectedContent);
                using (HttpResponseMessage response = await client.SendAsync(TestAsync, request))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    TestHelper.VerifyRequestMethod(response, method);
                    string responseContent = await response.Content.ReadAsStringAsync();
                    _output.WriteLine(responseContent);

                    Assert.Contains($"\"Content-Length\": \"{request.Content.Headers.ContentLength.Value}\"", responseContent);
                    TestHelper.VerifyResponseBody(
                        responseContent,
                        response.Content.Headers.ContentMD5,
                        false,
                        ExpectedContent);
                }
            }
        }

        [OuterLoop("Uses external server")]
        [Theory]
        [InlineData("12345678910", 0)]
        [InlineData("12345678910", 5)]
        public async Task SendAsync_SendSameRequestMultipleTimesDirectlyOnHandler_Success(string stringContent, int startingPosition)
        {
            using (var handler = new HttpMessageInvoker(CreateHttpClientHandler()))
            {
                byte[] byteContent = Encoding.ASCII.GetBytes(stringContent);
                var content = new MemoryStream();
                content.Write(byteContent, 0, byteContent.Length);
                content.Position = startingPosition;
                var request = new HttpRequestMessage(HttpMethod.Post, Configuration.Http.RemoteEchoServer) { Content = new StreamContent(content), Version = UseVersion };

                for (int iter = 0; iter < 2; iter++)
                {
                    using (HttpResponseMessage response = await handler.SendAsync(TestAsync, request, CancellationToken.None))
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                        string responseContent = await response.Content.ReadAsStringAsync();

                        Assert.Contains($"\"Content-Length\": \"{request.Content.Headers.ContentLength.Value}\"", responseContent);

                        Assert.Contains(stringContent.Substring(startingPosition), responseContent);
                        if (startingPosition != 0)
                        {
                            Assert.DoesNotContain(stringContent.Substring(0, startingPosition), responseContent);
                        }
                    }
                }
            }
        }

        [OuterLoop("Uses external server")]
        [Theory, MemberData(nameof(HttpMethodsThatDontAllowContent))]
        public async Task SendAsync_SendRequestUsingNoBodyMethodToEchoServerWithContent_NoBodySent(
            string method,
            Uri serverUri)
        {
            using (HttpClient client = CreateHttpClient())
            {
                var request = new HttpRequestMessage(
                    new HttpMethod(method),
                    serverUri)
                {
                    Content = new StringContent(ExpectedContent),
                    Version = UseVersion
                };

                using (HttpResponseMessage response = await client.SendAsync(TestAsync, request))
                {
                    if (method == "TRACE")
                    {
                        // .NET Framework also allows the HttpWebRequest and HttpClient APIs to send a request using 'TRACE'
                        // verb and a request body. The usual response from a server is "400 Bad Request".
                        // See here for more info: https://github.com/dotnet/runtime/issues/17475
                        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    }
                    else
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        TestHelper.VerifyRequestMethod(response, method);
                        string responseContent = await response.Content.ReadAsStringAsync();
                        Assert.DoesNotContain(ExpectedContent, responseContent);
                    }
                }
            }
        }
#endregion

#region Version tests
        [OuterLoop("Uses external server")]
        [Fact]
        public async Task SendAsync_RequestVersion10_ServerReceivesVersion10Request()
        {
            // Test is not supported for WinHttpHandler and HTTP/2
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            Version receivedRequestVersion = await SendRequestAndGetRequestVersionAsync(new Version(1, 0));
            Assert.Equal(new Version(1, 0), receivedRequestVersion);
        }

        [OuterLoop("Uses external server")]
        [Fact]
        public async Task SendAsync_RequestVersion11_ServerReceivesVersion11Request()
        {
            Version receivedRequestVersion = await SendRequestAndGetRequestVersionAsync(new Version(1, 1));
            Assert.Equal(new Version(1, 1), receivedRequestVersion);
        }

        [OuterLoop("Uses external server")]
        [Fact]
        public async Task SendAsync_RequestVersionNotSpecified_ServerReceivesVersion11Request()
        {
            // SocketsHttpHandler treats 0.0 as a bad version, and throws.
            if (!IsWinHttpHandler)
            {
                return;
            }

            // The default value for HttpRequestMessage.Version is Version(1,1).
            // So, we need to set something different (0,0), to test the "unknown" version.
            Version receivedRequestVersion = await SendRequestAndGetRequestVersionAsync(new Version(0, 0));
            Assert.Equal(new Version(1, 1), receivedRequestVersion);
        }

        [OuterLoop("Uses external server")]
        [Theory]
        [MemberData(nameof(Http2Servers))]
        public async Task SendAsync_RequestVersion20_ResponseVersion20IfHttp2Supported(Uri server)
        {
            // Sync API supported only up to HTTP/1.1
            if (!TestAsync)
            {
                return;
            }

            // We don't currently have a good way to test whether HTTP/2 is supported without
            // using the same mechanism we're trying to test, so for now we allow both 2.0 and 1.1 responses.
            var request = new HttpRequestMessage(HttpMethod.Get, server);
            request.Version = new Version(2, 0);

            using (HttpClient client = CreateHttpClient())
            {
                // It is generally expected that the test hosts will be trusted, so we don't register a validation
                // callback in the usual case.

                using (HttpResponseMessage response = await client.SendAsync(TestAsync, request))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.True(
                        response.Version == new Version(2, 0) ||
                        response.Version == new Version(1, 1),
                        "Response version " + response.Version);
                }
            }
        }

        [Fact]
        public async Task SendAsync_RequestVersion20_HttpNotHttps_NoUpgradeRequest()
        {
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            // Sync API supported only up to HTTP/1.1
            if (!TestAsync)
            {
                return;
            }

            await LoopbackServerFactory.CreateClientAndServerAsync(async uri =>
            {
                using (HttpClient client = CreateHttpClient())
                {
                    (await client.SendAsync(TestAsync, new HttpRequestMessage(HttpMethod.Get, uri) { Version = new Version(2, 0) })).Dispose();
                }
            }, async server =>
            {
                HttpRequestData requestData = await server.AcceptConnectionSendResponseAndCloseAsync();
                Assert.Equal(0, requestData.GetHeaderValues("Upgrade").Length);
            });
        }

        [OuterLoop("Uses external server")]
        [ConditionalTheory(nameof(IsWindows10Version1607OrGreater)), MemberData(nameof(Http2NoPushServers))]
        public async Task SendAsync_RequestVersion20_ResponseVersion20(Uri server)
        {
            // Sync API supported only up to HTTP/1.1
            if (!TestAsync)
            {
                return;
            }

            _output.WriteLine(server.AbsoluteUri.ToString());
            var request = new HttpRequestMessage(HttpMethod.Get, server);
            request.Version = new Version(2, 0);

            using (HttpClient client = CreateHttpClient())
            {
                using (HttpResponseMessage response = await client.SendAsync(TestAsync, request))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(new Version(2, 0), response.Version);
                }
            }
        }

        private async Task<Version> SendRequestAndGetRequestVersionAsync(Version requestVersion)
        {
            Version receivedRequestVersion = null;

            await LoopbackServer.CreateServerAsync(async (server, url) =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Version = requestVersion;

                using (HttpClient client = CreateHttpClient())
                {
                    Task<HttpResponseMessage> getResponse = client.SendAsync(TestAsync, request);
                    Task<List<string>> serverTask = server.AcceptConnectionSendResponseAndCloseAsync();
                    await TestHelper.WhenAllCompletedOrAnyFailed(getResponse, serverTask);

                    List<string> receivedRequest = await serverTask;
                    using (HttpResponseMessage response = await getResponse)
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    }

                    string statusLine = receivedRequest[0];
                    if (statusLine.Contains("/1.0"))
                    {
                        receivedRequestVersion = new Version(1, 0);
                    }
                    else if (statusLine.Contains("/1.1"))
                    {
                        receivedRequestVersion = new Version(1, 1);
                    }
                    else
                    {
                        Assert.True(false, "Invalid HTTP request version");
                    }
                }
            });

            return receivedRequestVersion;
        }
        #endregion

        #region Uri wire transmission encoding tests
        [Fact]
        public async Task SendRequest_UriPathHasReservedChars_ServerReceivedExpectedPath()
        {
            if (IsWinHttpHandler && UseVersion >= HttpVersion20.Value)
            {
                return;
            }

            await LoopbackServerFactory.CreateServerAsync(async (server, rootUrl) =>
            {
                var uri = new Uri($"{rootUrl.Scheme}://{rootUrl.Host}:{rootUrl.Port}/test[]");
                _output.WriteLine(uri.AbsoluteUri.ToString());

                using (HttpClient client = CreateHttpClient())
                {
                    Task<HttpResponseMessage> getResponseTask = client.GetAsync(uri);
                    Task<HttpRequestData> serverTask = server.AcceptConnectionSendResponseAndCloseAsync();

                    await TestHelper.WhenAllCompletedOrAnyFailed(getResponseTask, serverTask);
                    HttpRequestData receivedRequest = await serverTask;
                    using (HttpResponseMessage response = await getResponseTask)
                    {
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        Assert.Equal(uri.PathAndQuery, receivedRequest.Path);
                    }
                }
            });
        }
#endregion

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsNotWindowsSubsystemForLinux))] // [ActiveIssue("https://github.com/dotnet/runtime/issues/18258")]
        public async Task GetAsync_InvalidUrl_ExpectedExceptionThrown()
        {
            string invalidUri = $"http://{Configuration.Sockets.InvalidHost}";
            _output.WriteLine($"{DateTime.Now} connecting to {invalidUri}");
            using (HttpClient client = CreateHttpClient())
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(invalidUri));
                await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(invalidUri));
            }
        }
    }
}
