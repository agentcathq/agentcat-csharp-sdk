using System;
using AgentCat.Internal.Logging;
using Microsoft.Extensions.DependencyInjection;
#if NET
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;
#endif

namespace AgentCat.Internal.Http;

/// <summary>
/// Reads the current ASP.NET Core request without a reference to ASP.NET Core. The package finds ASP.NET Core at run
/// time by constant type names; without it (stdio, console apps, .NET Framework hosts) every read gives nothing.
/// </summary>
internal static class AspNetCoreRequest
{
#if NET
    internal const string IHttpContextAccessorType = "Microsoft.AspNetCore.Http.IHttpContextAccessor, Microsoft.AspNetCore.Http.Abstractions";

    internal const string HttpContextAccessorType = "Microsoft.AspNetCore.Http.HttpContextAccessor, Microsoft.AspNetCore.Http";

    internal const string HttpContextType = "Microsoft.AspNetCore.Http.HttpContext, Microsoft.AspNetCore.Http.Abstractions";

    internal const string HttpRequestType = "Microsoft.AspNetCore.Http.HttpRequest, Microsoft.AspNetCore.Http.Abstractions";

    internal const string UriHelperType = "Microsoft.AspNetCore.Http.Extensions.UriHelper, Microsoft.AspNetCore.Http.Extensions";

    private static readonly Lazy<Reader> Default = new(static () => new Reader(Names.Default));

    /// <summary>True when ASP.NET Core is loaded and every member the reader uses was found.</summary>
    internal static bool IsAvailable => Default.Value.IsAvailable;

    /// <summary>
    /// Copies the headers and URL of the current request. <see langword="null"/> when there is none, when the
    /// request has ended or the accessor gives another request after the copy, or on any failure. Never throws.
    /// </summary>
    /// <param name="services">The call's services; their <c>IHttpContextAccessor</c> comes first.</param>
    /// <param name="log">Takes the one line written when a member is missing.</param>
    internal static RequestCapture? Capture(IServiceProvider? services, IAgentCatLog log) => Default.Value.Capture(services, log);

    /// <summary>Registers <c>HttpContextAccessor</c> unless the app has an <c>IHttpContextAccessor</c>. Never throws.</summary>
    internal static void TryRegisterAccessor(IServiceCollection services) => Default.Value.TryRegisterAccessor(services);

    /// <summary>The type and member names the reader looks up. Tests replace one to see it missing.</summary>
    internal sealed record Names
    {
        internal static readonly Names Default = new();

        internal string AccessorType { get; init; } = IHttpContextAccessorType;

        internal string AccessorClassType { get; init; } = HttpContextAccessorType;

        internal string ContextType { get; init; } = HttpContextType;

        internal string RequestType { get; init; } = HttpRequestType;

        internal string HttpContext { get; init; } = "HttpContext";

        internal string Request { get; init; } = "Request";

        internal string Headers { get; init; } = "Headers";

        internal string Scheme { get; init; } = "Scheme";

        internal string Host { get; init; } = "Host";

        internal string PathBase { get; init; } = "PathBase";

        internal string Path { get; init; } = "Path";

        internal string QueryString { get; init; } = "QueryString";

        internal string GetEncodedUrl { get; init; } = "GetEncodedUrl";
    }

    // The types come from constant names and reach parameters that ask for their public properties (UriHelper: public
    // methods), so trimming and Native AOT keep those members in apps that have ASP.NET Core, and find nothing to keep
    // and nothing to warn about in apps that do not. A [DynamicDependency] would warn IL2035 in every trimmed app
    // without ASP.NET Core. Typed delegates cannot bind without the types, so the reads go through PropertyInfo.
    internal sealed class Reader
    {
        private const string Off = "Warning: AgentCat HTTP request capture is off: ";

        private readonly Type? _accessorType;

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        private readonly Type? _accessorClass;

        private readonly object? _cachedAccessor;
        private readonly PropertyInfo? _httpContext;
        private readonly PropertyInfo? _request;
        private readonly PropertyInfo? _headers;
        private readonly PropertyInfo? _scheme;
        private readonly PropertyInfo? _host;
        private readonly PropertyInfo? _pathBase;
        private readonly PropertyInfo? _path;
        private readonly PropertyInfo? _queryString;
        private readonly MethodInfo? _encodedUrl;
        private int _reported;

        internal Reader(Names names)
        {
            Type? accessorType;
            Type? accessorClass;
            Type? contextType;
            Type? requestType;
            Type? uriHelper;

            // Type.GetType takes only the constant names, so trimming keeps those types, and a name that a test replaced
            // is not found. IHttpContextAccessor is the probe: without it the app has no ASP.NET Core, and nothing else
            // is looked up or reported. HttpContextAccessor is a probe too: an app with only the abstractions assembly
            // is not an ASP.NET Core app.
            try
            {
                accessorType = names.AccessorType == IHttpContextAccessorType ? Type.GetType(IHttpContextAccessorType) : null;
                if (accessorType is null)
                {
                    return;
                }

                accessorClass = names.AccessorClassType == HttpContextAccessorType ? Type.GetType(HttpContextAccessorType) : null;
                if (accessorClass is null)
                {
                    return;
                }

                contextType = names.ContextType == HttpContextType ? Type.GetType(HttpContextType) : null;
                requestType = names.RequestType == HttpRequestType ? Type.GetType(HttpRequestType) : null;
                uriHelper = Type.GetType(UriHelperType);
            }
            catch (Exception)
            {
                return;
            }

            if (contextType is null || requestType is null)
            {
                MissingMember = TypeName(contextType is null ? names.ContextType : names.RequestType);
                return;
            }

            string? missing = null;
            _httpContext = FindProperty(accessorType, names.HttpContext, ref missing);
            _request = FindProperty(contextType, names.Request, ref missing);
            _headers = FindProperty(requestType, names.Headers, ref missing);
            _scheme = FindProperty(requestType, names.Scheme, ref missing);
            _host = FindProperty(requestType, names.Host, ref missing);
            _pathBase = FindProperty(requestType, names.PathBase, ref missing);
            _path = FindProperty(requestType, names.Path, ref missing);
            _queryString = FindProperty(requestType, names.QueryString, ref missing);
            if (missing is null && !typeof(IEnumerable<KeyValuePair<string, StringValues>>).IsAssignableFrom(_headers!.PropertyType))
            {
                missing = requestType.FullName + "." + names.Headers;
            }

            if (missing is not null)
            {
                MissingMember = missing;
                return;
            }

            _accessorType = accessorType;
            _accessorClass = accessorClass;
            _encodedUrl = FindEncodedUrl(uriHelper, names.GetEncodedUrl, requestType);
            _cachedAccessor = CreateAccessor(accessorClass);
            IsAvailable = true;
        }

        /// <summary>True when ASP.NET Core is loaded and every member was found.</summary>
        internal bool IsAvailable { get; }

        /// <summary>
        /// The first type or member that was not found once <c>IHttpContextAccessor</c> was, as <c>Type</c> or
        /// <c>Type.Member</c>.
        /// </summary>
        internal string? MissingMember { get; }

        internal RequestCapture? Capture(IServiceProvider? services, IAgentCatLog log)
        {
            if (!IsAvailable)
            {
                if (MissingMember is not null && Interlocked.Exchange(ref _reported, 1) == 0)
                {
                    log.Write(Off + MissingMember + " was not found");
                }

                return null;
            }

            try
            {
                var accessor = services?.GetService(_accessorType!) ?? _cachedAccessor;
                var context = accessor is null ? null : _httpContext!.GetValue(accessor);
                var request = context is null ? null : _request!.GetValue(context);
                if (request is null)
                {
                    return null;
                }

                var headers = (IEnumerable<KeyValuePair<string, StringValues>>?)_headers!.GetValue(request);
                var capture = RequestCapture.Create(headers, Url(request));

                // ASP.NET Core clears the accessor before it reuses a context for the next request on the connection,
                // so another context now means that the copy can mix two requests.
                return ReferenceEquals(_httpContext!.GetValue(accessor), context) ? capture : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal void TryRegisterAccessor(IServiceCollection services)
        {
            if (!IsAvailable || _accessorType is null || _accessorClass is null)
            {
                return;
            }

            try
            {
                services.TryAdd(ServiceDescriptor.Singleton(_accessorType, _accessorClass));
            }
            catch (Exception)
            {
                // The app keeps its own registrations; capture needs an accessor that it registers.
            }
        }

        // "Namespace.Type, Assembly" without the assembly.
        private static string TypeName(string assemblyQualifiedName)
        {
            var comma = assemblyQualifiedName.IndexOf(',', StringComparison.Ordinal);
            return comma < 0 ? assemblyQualifiedName : assemblyQualifiedName.Substring(0, comma);
        }

        private static PropertyInfo? FindProperty(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type,
            string name,
            ref string? missing)
        {
            if (missing is not null)
            {
                return null;
            }

            PropertyInfo? found;
            try
            {
                found = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception)
            {
                found = null;
            }

            if (found?.GetMethod is null)
            {
                missing = type.FullName + "." + name;
                return null;
            }

            return found;
        }

        private static MethodInfo? FindEncodedUrl(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type? uriHelper,
            string name,
            Type requestType)
        {
            try
            {
                var method = uriHelper?.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, new[] { requestType }, null);
                return method?.ReturnType == typeof(string) ? method : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static object? CreateAccessor([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type accessorClass)
        {
            try
            {
                return Activator.CreateInstance(accessorClass);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // UriHelper.GetEncodedUrl when it was found, else the same string from the parts (UriHelper.BuildAbsolute). The
        // host keeps its case. No host, no URL.
        private string? Url(object request)
        {
            var host = _host!.GetValue(request)?.ToString();
            if (string.IsNullOrEmpty(host))
            {
                return null;
            }

            if (_encodedUrl is not null)
            {
                return _encodedUrl.Invoke(null, new[] { request }) as string;
            }

            var scheme = _scheme!.GetValue(request) as string ?? string.Empty;
            var pathBase = _pathBase!.GetValue(request)?.ToString() ?? string.Empty;
            var path = _path!.GetValue(request)?.ToString() ?? string.Empty;
            var query = _queryString!.GetValue(request)?.ToString() ?? string.Empty;
            if (path.Length == 0)
            {
                if (pathBase.Length == 0)
                {
                    path = "/";
                }
            }
            else if (pathBase.EndsWith('/'))
            {
                pathBase = pathBase.Substring(0, pathBase.Length - 1);
            }

            return scheme + Uri.SchemeDelimiter + host + pathBase + path + query;
        }
    }
#else
    internal static bool IsAvailable => false;

    internal static RequestCapture? Capture(IServiceProvider? services, IAgentCatLog log) => null;

    internal static void TryRegisterAccessor(IServiceCollection services)
    {
    }
#endif
}
