using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Nancy;
using Nancy.Responses.Negotiation;
using Newtonsoft.Json;

namespace LiGet.Cache.Proxy
{
    public class CachingProxyV3NancyModule : NancyModule
    {
        private static readonly log4net.ILog _log = log4net.LogManager.GetLogger(typeof(CachingProxyV3NancyModule));

        static readonly string basePath = "/api/cache";
        private IHttpClient client;
        private IV3JsonInterceptor interceptor;
        private readonly IUrlReplacementsProvider replacementsProvider;
        private readonly IPackageMetadataCache metadataCache;

        public CachingProxyV3NancyModule(IHttpClient client, IV3JsonInterceptor interceptor,
            IUrlReplacementsProvider replacementsProvider, INupkgCacheProvider nupkgCache,
            IPackageMetadataCache metadataCache)
            : base(basePath)
        {
            this.metadataCache = metadataCache;
            this.client = client;
            this.interceptor = interceptor;
            this.replacementsProvider = replacementsProvider;

            this.OnError.AddItemToEndOfPipeline(HandleError);

            base.Get<Response>("/v3/registration3/{package}/index.json", async args =>
            {
                string package = args.package;
                byte[] bytes = this.metadataCache.TryGet(package);
                if (bytes != null)
                {
                    _log.DebugFormat("Cache hit. Serving {0} from cache", Request.Url);
                    return new Response()
                    {
                        StatusCode = Nancy.HttpStatusCode.OK,
                        ContentType = "application/json",
                        Contents = netStream =>
                        {
                            try
                            {
                                netStream.Write(bytes, 0, bytes.Length);
                                netStream.Flush();
                                _log.DebugFormat("Served {0} bytes from cache", bytes.Length);
                            }
                            catch (Exception ex)
                            {
                                _log.Error("Something went wrong when serving query from cache", ex);
                                throw new Exception("Serving query from cache failed", ex);
                            }
                        }
                    };
                }
                string myV3Url = this.GetServiceUrl().AbsoluteUri;
                Dictionary<string, string> replacements = replacementsProvider.GetReplacements(myV3Url);
                var request = new HttpRequestMessage()
                {
                    RequestUri = replacementsProvider.GetOriginUri(Request.Url),
                    Method = HttpMethod.Get,
                };
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                _log.DebugFormat("Cache miss. Proxying {0} to {1}", Request.Url, request.RequestUri);
                var timestamp = DateTimeOffset.UtcNow;
                var originalResponse = await client.SendAsync(request);
                return new DisposingResponse(originalResponse)
                {
                    StatusCode = (Nancy.HttpStatusCode)(int)originalResponse.StatusCode,
                    ContentType = originalResponse.Content.Headers.ContentType.MediaType,
                    Contents = netStream =>
                    {
                        Stream originalStream = null;
                        try
                        {
                            originalStream = originalResponse.Content.ReadAsStreamAsync().Result;
                            if (originalResponse.Content.Headers.ContentEncoding.Contains("gzip"))
                                originalStream = new GZipStream(originalStream, CompressionMode.Decompress);
                            using (var ms = new MemoryStream())
                            {
                                interceptor.Intercept(replacements, originalStream, ms);
                                bytes = ms.ToArray();
                                this.metadataCache.Insert(package, timestamp, bytes);
                                netStream.Write(bytes, 0, bytes.Length);
                                netStream.Flush();
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Error("Something went wrong when intercepting origin response", ex);
                            throw new Exception("Intercepting origins response failed", ex);
                        }
                        finally {
                            if(originalStream != null)
                                originalStream.Dispose();
                            originalResponse.Dispose();
                        }
                    }
                };
            });

            base.Get<Response>("/{path*}", async args =>
            {
                string myV3Url = this.GetServiceUrl().AbsoluteUri;
                Dictionary<string, string> replacements = replacementsProvider.GetReplacements(myV3Url);
                var request = new HttpRequestMessage()
                {
                    RequestUri = replacementsProvider.GetOriginUri(this.Request.Url),
                    Method = HttpMethod.Get,
                };
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                _log.DebugFormat("Proxying {0} to {1}", this.Request.Url, request.RequestUri);
                var originalResponse = await client.SendAsync(request);
                if(!(200 <= (int)originalResponse.StatusCode && (int)originalResponse.StatusCode < 300))
                    _log.WarnFormat("Origin server {0} response status is {1}",request.RequestUri, originalResponse.StatusCode);
                return new DisposingResponse(originalResponse)
                {
                    StatusCode = (Nancy.HttpStatusCode)(int)originalResponse.StatusCode,
                    ContentType = originalResponse.Content.Headers.ContentType.MediaType,
                    Contents = netStream =>
                    {
                        Stream originalStream = null;
                        try
                        {
                            originalStream = originalResponse.Content.ReadAsStreamAsync().Result;
                            if (originalResponse.Content.Headers.ContentEncoding.Contains("gzip"))
                                originalStream = new GZipStream(originalStream, CompressionMode.Decompress);
                            if(new MediaRange("application/json").Matches(new MediaRange(originalResponse.Content.Headers.ContentType.MediaType)))
                                interceptor.Intercept(replacements, originalStream, netStream);
                            else
                                originalStream.CopyTo(netStream);                            
                        }
                        catch (Exception ex)
                        {
                            _log.Error("Something went wrong when intercepting origin response", ex);
                            throw new Exception("Intercepting origins response failed", ex);
                        }
                        finally {
                            if(originalStream != null)
                                originalStream.Dispose();
                            originalResponse.Dispose();
                        }
                    }
                };
            });

            base.Get<Response>("/v3-flatcontainer/{package}/{version}/{filename}", async args =>
            {
                string package = args.package;
                string version = args.version;
                string path = package + "/" + version + "/" + args.filename;
                byte[] hit;
                using (var tx = nupkgCache.OpenTransaction())
                {
                    hit = tx.TryGet(args.package, args.version);
                }
                if (hit == null)
                { // cache miss
                    var request = new HttpRequestMessage()
                    {
                        RequestUri = replacementsProvider.GetOriginUri(this.Request.Url),
                        Method = HttpMethod.Get,
                    };
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                    _log.DebugFormat("Cache miss. Proxying {0} to {1}", this.Request.Url, request.RequestUri);
                    var originalResponse = await client.SendAsync(request);
                    return new DisposingResponse(originalResponse)
                    {
                        ContentType = originalResponse.Content.Headers.ContentType.MediaType,
                        Contents = netStream =>
                        {
                            Stream originalStream = null;
                            try
                            {
                                using (var cacheTx = nupkgCache.OpenTransaction())
                                {
                                    originalStream = originalResponse.Content.ReadAsStreamAsync().Result;
                                    using (var ms = new MemoryStream((int)originalResponse.Content.Headers.ContentLength.GetValueOrDefault(4096)))
                                    {
                                        originalStream.CopyTo(ms);
                                        byte[] value = ms.ToArray();
                                        var writing = netStream.WriteAsync(value, 0, value.Length);
                                        try
                                        {
                                            cacheTx.Insert(package, version, value);
                                        }
                                        catch (Exception cacheError)
                                        {
                                            _log.Error("Failed to insert package to cache", cacheError);
                                        }
                                        finally
                                        {
                                            writing.Wait();
                                            netStream.Flush();
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _log.Error("Something went wrong when serving nupkg", ex);
                                throw new Exception("Serving nupkg failed", ex);
                            }
                            finally {
                                if(originalStream != null)
                                    originalStream.Dispose();
                                originalResponse.Dispose();
                            }
                        }
                    };
                }
                else
                { // cache hit, return from cache
                    _log.DebugFormat("Cache hit. Serving {0} from cache", this.Request.Url);
                    return new DisposingResponse()
                    {
                        ContentType = "application/octet-stream",
                        Contents = netStream =>
                        {
                            try
                            {
                                netStream.Write(hit, 0, hit.Length);
                                netStream.Flush();
                            }
                            catch (Exception ex)
                            {
                                _log.Error("Something went wrong when serving nupkg from cache", ex);
                                throw new Exception("Serving nupkg from cache failed", ex);
                            }
                        }
                    };
                }
            });

            base.Head<object>(@"^(.*)$", new Func<dynamic, object>(ThrowNotSupported));
            base.Post<object>(@"^(.*)$", new Func<dynamic, object>(ThrowNotSupported));
            base.Put<object>(@"^(.*)$", new Func<dynamic, object>(ThrowNotSupported));
            base.Delete<object>(@"^(.*)$", new Func<dynamic, object>(ThrowNotSupported));
        }

        private object ThrowNotSupported(dynamic args)
        {
            throw new NotSupportedException(string.Format("Not supported endpoint path {0}", base.Request.Path));
        }

        public dynamic HandleError(NancyContext ctx, Exception ex)
        {
            string path = ctx.Request.Path;
            _log.ErrorFormat("Error handling proxy request {0}\n{1}", path, ex);
            var notSupported = ex as NotSupportedException;
            if (notSupported != null)
            {
                if (_log.IsDebugEnabled)
                {
                    string method = ctx.Request.Method;
                    string body = "";
                    if (ctx.Request.Body != null)
                    {
                        body = new StreamReader(ctx.Request.Body).ReadToEnd();
                    }
                    StringBuilder headers = new StringBuilder();
                    foreach (var h in ctx.Request.Headers)
                    {
                        headers.Append(h.Key).Append(": ").Append(string.Join(" ", h.Value)).AppendLine();
                    }
                    _log.DebugFormat("Not supported proxy request content: {0} {1}\nHeaders:\n{2}\nBody:\n{3}", method, ctx.Request.Url, headers, body);
                }
                return HttpStatusCode.NotImplemented;
            }
            return null;
        }

        public Uri GetServiceUrl()
        {
            return new Uri(new Uri(base.Request.Url).GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped) + basePath + "/v3");
        }
    }
}