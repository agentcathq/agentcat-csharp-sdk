using System;
using System.Collections.Generic;
using System.Diagnostics;
#if !NET9_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
#endif
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCat.Internal.Json;
using AgentCat.Internal.Text;

namespace AgentCat.Internal.Errors;

internal static class ExceptionCapture
{
    internal const string Platform = "csharp";
    internal const int MaxFrames = 50;
    internal const int MaxChainDepth = 10;

    // Exception.ToString recurses through InnerException, so a cyclic or very long chain would overflow the stack.
    private const int MaxStackChainLength = 100;

    private const string Unknown = "<unknown>";
    private const string UnknownResultMessage = "Unknown error";
    private const string SdkNamespacePrefix = "AgentCat.";

    private static readonly string[] FrameworkPrefixes = { "System.", "Microsoft.", "ModelContextProtocol." };

    private static readonly string[] FrameworkAssemblies = { "System", "mscorlib", "netstandard", "ModelContextProtocol" };

    private static readonly string[] SdkAssemblies = { "AgentCat", "AgentCat.Api" };

    internal static JsonObject Capture(Exception exception) => Capture(exception, new SourceLines());

    internal static JsonObject Capture(Exception exception, SourceLines sources)
    {
        var result = new JsonObject();
        if (exception is null)
        {
            result["message"] = "null";
            result["platform"] = Platform;
            return result;
        }

        result["message"] = MessageOf(exception);
        AddString(result, "type", TypeNameOf(exception));
        result["platform"] = Platform;

        try
        {
            var libraries = AppLibraries.Current;
            var currentDirectory = CurrentDirectory();
            AddDetails(result, exception, sources, libraries, currentDirectory);
            var chain = ChainOf(exception, sources, libraries, currentDirectory);
            if (chain.Count > 0)
            {
                result["chained_errors"] = chain;
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    internal static JsonObject CaptureFromResult(JsonNode? result)
    {
        string message;
        try
        {
            message = ResultMessageOf(result);
        }
        catch (Exception)
        {
            message = UnknownResultMessage;
        }

        return new JsonObject { ["message"] = message, ["platform"] = Platform };
    }

    internal static string FunctionOf(string? declaringTypeName, string? methodName)
    {
        if (methodName is null || methodName.Length == 0)
        {
            return Unknown;
        }

        var function = SourceName(StateMachineSourceName(declaringTypeName) ?? methodName);
        var end = LambdaMethodEnd(function, 0, function.Length);
        return end > 0 ? function.Substring(0, end) : function;
    }

    // Compiled expression trees and dynamic methods are named lambda_method<N>, where N differs per process.
    // Returns the length of the name without N when [start, start + length) is exactly lambda_method<digits>, else 0.
    private static int LambdaMethodEnd(string text, int start, int length)
    {
        const string Name = "lambda_method";
        if (length <= Name.Length || string.CompareOrdinal(text, start, Name, 0, Name.Length) != 0)
        {
            return 0;
        }

        for (var i = start + Name.Length; i < start + length; i++)
        {
            if (text[i] < '0' || text[i] > '9')
            {
                return 0;
            }
        }

        return Name.Length;
    }

    internal static string StableStack(string stack)
    {
        const string Name = "lambda_method";
        if (stack.IndexOf(Name, StringComparison.Ordinal) < 0)
        {
            return stack;
        }

        var builder = new StringBuilder(stack.Length);
        var lineStart = 0;
        while (lineStart <= stack.Length)
        {
            var lineEnd = stack.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = stack.Length;
            }

            var line = stack.Substring(lineStart, lineEnd - lineStart);
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("at ", StringComparison.Ordinal))
            {
                line = StableFrameLine(line, Name);
            }

            builder.Append(line);
            if (lineEnd < stack.Length)
            {
                builder.Append('\n');
            }

            lineStart = lineEnd + 1;
        }

        return builder.ToString();
    }

    // A stack line is "at Ns.Type.Method(args) in file:line"; only the method name before its first '(' is rewritten.
    private static string StableFrameLine(string line, string name)
    {
        var open = line.IndexOf('(');
        if (open < 0)
        {
            return line;
        }

        var nameStart = open;
        while (nameStart > 0 && (char.IsLetterOrDigit(line[nameStart - 1]) || line[nameStart - 1] == '_'))
        {
            nameStart--;
        }

        return LambdaMethodEnd(line, nameStart, open - nameStart) > 0
            ? line.Remove(nameStart + name.Length, open - nameStart - name.Length)
            : line;
    }

    internal static string? ModuleOf(string? declaringTypeName)
    {
        if (declaringTypeName is null || declaringTypeName.Length == 0)
        {
            return null;
        }

        var segments = WithoutGenericArguments(declaringTypeName).Split('+');
        var module = new StringBuilder(WithoutFileLocalPrefix(segments[0]));
        for (var i = 1; i < segments.Length; i++)
        {
            if (segments[i].Length > 0 && segments[i][0] != '<')
            {
                module.Append('+').Append(segments[i]);
            }
        }

        return module.ToString();
    }

    private static void AddDetails(
        JsonObject target,
        Exception exception,
        SourceLines sources,
        AppLibraries libraries,
        string? currentDirectory)
    {
        AddString(target, "stack", StackOf(exception));
        var frames = FramesOf(exception, sources, libraries, currentDirectory);
        if (frames is not null)
        {
            target["frames"] = frames;
        }
    }

    private static JsonArray ChainOf(Exception exception, SourceLines sources, AppLibraries libraries, string? currentDirectory)
    {
        var chain = new JsonArray();
        var seen = new List<Exception> { exception };
        var current = exception.InnerException;
        while (current is not null && chain.Count < MaxChainDepth && !ContainsReference(seen, current))
        {
            seen.Add(current);
            var entry = new JsonObject { ["message"] = MessageOf(current) };
            AddString(entry, "type", TypeNameOf(current));
            AddDetails(entry, current, sources, libraries, currentDirectory);
            chain.Add((JsonNode)entry);
            current = current.InnerException;
        }

        return chain;
    }

    private static bool ContainsReference(List<Exception> seen, Exception candidate)
    {
        foreach (var exception in seen)
        {
            if (ReferenceEquals(exception, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static string MessageOf(Exception exception)
    {
        try
        {
            return exception.Message ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string? TypeNameOf(Exception exception)
    {
        try
        {
            var type = exception.GetType();
            return type.IsConstructedGenericType ? type.ToString() : type.FullName ?? type.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? StackOf(Exception exception)
    {
        try
        {
            var current = exception.InnerException;
            for (var length = 0; current is not null; length++)
            {
                if (length >= MaxStackChainLength)
                {
                    return null;
                }

                current = current.InnerException;
            }

            return StableStack(exception.ToString());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static JsonArray? FramesOf(Exception exception, SourceLines sources, AppLibraries libraries, string? currentDirectory)
    {
        var stackFrames = StackFramesOf(exception);
        if (stackFrames is null || stackFrames.Length == 0)
        {
            return null;
        }

        return FramesFrom(stackFrames.Select(InfoOf), sources, libraries, currentDirectory);
    }

    // A frame with neither a method name nor a file (NativeAOT without stack trace data) gives the backend nothing
    // to group by, so it is dropped; with no frame left, frames stay out and the backend groups by message.
    internal static JsonArray? FramesFrom(
        IEnumerable<FrameInfo?> stackFrames,
        SourceLines sources,
        AppLibraries libraries,
        string? currentDirectory)
    {
        var frames = new JsonArray();
        foreach (var stackFrame in stackFrames)
        {
            if (frames.Count >= MaxFrames)
            {
                break;
            }

            if (stackFrame is not null
                && !IsReflectionInvoker(stackFrame)
                && (!string.IsNullOrEmpty(stackFrame.MethodName) || stackFrame.FileName is not null)
                && FrameOf(stackFrame, sources, libraries, currentDirectory) is { } frame)
            {
                frames.Add((JsonNode)frame);
            }
        }

        return frames.Count == 0 ? null : frames;
    }

    // The first call of a reflection-invoked method runs through the interpreter and later calls through an emitted
    // stub; leaving these frames out gives both the same trace.
    private static bool IsReflectionInvoker(FrameInfo info)
    {
        var module = ModuleOf(info.TypeName);
        return (module is not null
                && (module.StartsWith("System.Reflection.", StringComparison.Ordinal)
                    || string.Equals(module, "System.RuntimeMethodHandle", StringComparison.Ordinal)))
            || (info.MethodName is not null && info.MethodName.StartsWith("InvokeStub_", StringComparison.Ordinal));
    }

    private static StackFrame[]? StackFramesOf(Exception exception)
    {
        try
        {
            return new StackTrace(exception, fNeedFileInfo: true).GetFrames();
        }
        catch (Exception)
        {
            try
            {
                return new StackTrace(exception, fNeedFileInfo: false).GetFrames();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static FrameInfo? InfoOf(StackFrame? stackFrame)
    {
        if (stackFrame is null)
        {
            return null;
        }

        try
        {
            var (typeName, methodName, assemblyName) = MethodOf(stackFrame);
            return new FrameInfo(
                typeName,
                methodName,
                assemblyName,
                FileNameOf(stackFrame),
                stackFrame.GetFileLineNumber(),
                stackFrame.GetFileColumnNumber());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static JsonObject? FrameOf(FrameInfo info, SourceLines sources, AppLibraries libraries, string? currentDirectory)
    {
        try
        {
            var module = ModuleOf(info.TypeName);
            var absPath = info.FileName;
            var inApp = IsInApp(module, info.AssemblyName, absPath, libraries);

            var frame = new JsonObject { ["function"] = FunctionOf(info.TypeName, info.MethodName) };
            AddString(frame, "module", module);
            frame["filename"] = absPath is null ? Unknown : StackPaths.MakeRelativePath(absPath, currentDirectory);
            AddString(frame, "abs_path", absPath);
            if (info.LineNumber > 0)
            {
                frame["lineno"] = info.LineNumber;
            }

            if (info.ColumnNumber > 0)
            {
                frame["colno"] = info.ColumnNumber;
            }

            frame["in_app"] = inApp;
            if (inApp && absPath is not null && sources.TryGetLine(absPath, info.LineNumber, out var line))
            {
                frame["context_line"] = line;
            }

            return frame;
        }
        catch (Exception)
        {
            return null;
        }
    }

#if NET9_0_OR_GREATER
    private static (string? TypeName, string? MethodName, string? AssemblyName) MethodOf(StackFrame stackFrame)
    {
        try
        {
            var method = DiagnosticMethodInfo.Create(stackFrame);
            return method is null ? default : (method.DeclaringTypeName, method.Name, SimpleAssemblyName(method.DeclaringAssemblyName));
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static string? SimpleAssemblyName(string? assemblyName)
    {
        var comma = assemblyName?.IndexOf(',', StringComparison.Ordinal) ?? -1;
        return comma < 0 ? assemblyName : assemblyName!.Substring(0, comma);
    }
#else
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "A trimmed method yields null and the frame falls back to <unknown>.")]
    private static (string? TypeName, string? MethodName, string? AssemblyName) MethodOf(StackFrame stackFrame)
    {
        try
        {
            var method = stackFrame.GetMethod();
            if (method is null)
            {
                return default;
            }

            var type = method.DeclaringType;
            return (type is null ? null : type.FullName ?? type.ToString(), method.Name, method.Module.Assembly.GetName().Name);
        }
        catch (Exception)
        {
            return default;
        }
    }
#endif

    private static string? FileNameOf(StackFrame stackFrame)
    {
        try
        {
            return stackFrame.GetFileName();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The SDK's own frames are never the app's. deps.json then tells the app's projects from NuGet packages; the
    // framework prefixes cover what it does not list (shared-framework assemblies, or every assembly when the app
    // has no deps.json), so a customer's own Microsoft.* project stays in-app when deps.json lists it.
    private static bool IsInApp(string? module, string? assemblyName, string? absPath, AppLibraries libraries)
    {
        if (module is null || IsSdk(module, assemblyName) || (absPath is not null && !StackPaths.IsInAppPath(absPath)))
        {
            return false;
        }

        return libraries.KindOf(assemblyName) switch
        {
            LibraryKind.Project => true,
            LibraryKind.Package => false,
            _ => !IsFramework(module, assemblyName),
        };
    }

    private static bool IsSdk(string module, string? assemblyName)
        => assemblyName is null
            ? module.StartsWith(SdkNamespacePrefix, StringComparison.Ordinal)
            : Array.IndexOf(SdkAssemblies, assemblyName) >= 0;

    private static bool IsFramework(string module, string? assemblyName)
        => StartsWithAny(module, FrameworkPrefixes)
            || (assemblyName is not null
                && (StartsWithAny(assemblyName, FrameworkPrefixes) || Array.IndexOf(FrameworkAssemblies, assemblyName) >= 0));

    private static bool StartsWithAny(string value, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? CurrentDirectory()
    {
        try
        {
            var directory = Environment.CurrentDirectory.Replace('\\', '/');
            return directory.Length == 0 || directory[directory.Length - 1] == '/' ? null : directory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? StateMachineSourceName(string? declaringTypeName)
    {
        if (declaringTypeName is null)
        {
            return null;
        }

        var typeName = WithoutGenericArguments(declaringTypeName);
        var innermost = typeName.Substring(typeName.LastIndexOf('+') + 1);
        return TrySplitGenerated(innermost, out var inner, out var suffix)
            && (suffix == "d" || suffix.StartsWith("d__", StringComparison.Ordinal))
                ? inner
                : null;
    }

    private static string SourceName(string name)
    {
        while (TrySplitGenerated(name, out var inner, out var suffix))
        {
            if (suffix.StartsWith("b__", StringComparison.Ordinal))
            {
                name = inner;
                continue;
            }

            if (suffix.StartsWith("g__", StringComparison.Ordinal))
            {
                var end = suffix.IndexOf('|');
                var local = end < 0 ? suffix.Substring(3) : suffix.Substring(3, end - 3);
                return local.Length > 0 ? local : name;
            }

            break;
        }

        return name;
    }

    private static bool TrySplitGenerated(string name, out string inner, out string suffix)
    {
        inner = string.Empty;
        suffix = string.Empty;
        if (name.Length < 3 || name[0] != '<')
        {
            return false;
        }

        var depth = 0;
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '<')
            {
                depth++;
            }
            else if (name[i] == '>' && --depth == 0)
            {
                if (i == 1)
                {
                    return false;
                }

                inner = name.Substring(1, i - 1);
                suffix = name.Substring(i + 1);
                return true;
            }
        }

        return false;
    }

    private static string WithoutGenericArguments(string typeName)
    {
        var result = new StringBuilder(typeName.Length);
        var brackets = 0;
        for (var i = 0; i < typeName.Length; i++)
        {
            var c = typeName[i];
            if (c == '[')
            {
                brackets++;
            }
            else if (c == ']')
            {
                brackets = Math.Max(0, brackets - 1);
            }
            else if (brackets == 0)
            {
                result.Append(c);
                if (c == '`')
                {
                    while (i + 1 < typeName.Length && typeName[i + 1] >= '0' && typeName[i + 1] <= '9')
                    {
                        result.Append(typeName[++i]);
                    }

                    i = SkipAngleArguments(typeName, i);
                }
            }
        }

        return result.ToString();
    }

    private static int SkipAngleArguments(string typeName, int index)
    {
        if (index + 1 >= typeName.Length || typeName[index + 1] != '<')
        {
            return index;
        }

        var depth = 0;
        for (var i = index + 1; i < typeName.Length; i++)
        {
            if (typeName[i] == '<')
            {
                depth++;
            }
            else if (typeName[i] == '>' && --depth == 0)
            {
                return i;
            }
        }

        return typeName.Length - 1;
    }

    private static string WithoutFileLocalPrefix(string segment)
    {
        var start = segment.IndexOf('<');
        if (start < 0 || !TrySplitGenerated(segment.Substring(start), out _, out var suffix))
        {
            return segment;
        }

        var separator = suffix.IndexOf("__", StringComparison.Ordinal);
        if (suffix.Length == 0 || suffix[0] != 'F' || separator < 1 || separator + 2 >= suffix.Length)
        {
            return segment;
        }

        for (var i = 1; i < separator; i++)
        {
            if (!Uri.IsHexDigit(suffix[i]))
            {
                return segment;
            }
        }

        return segment.Remove(start) + suffix.Remove(0, separator + 2);
    }

    private static void AddString(JsonObject target, string key, string? value)
    {
        if (value is not null)
        {
            target[key] = value;
        }
    }

    private static string ResultMessageOf(JsonNode? result)
    {
        if (result is null)
        {
            return "null";
        }

        var node = JsonTreeCopy.Expand(result);
        if (node is JsonObject obj
            && obj.ContainsKey("isError")
            && obj.TryGetPropertyValue("content", out var content)
            && content is not null
            && JsonTreeCopy.Expand(content) is JsonArray blocks)
        {
            var texts = new List<string>();
            foreach (var block in blocks)
            {
                if (block is not null && JsonTreeCopy.Expand(block) is JsonObject blockObject && IsTextBlock(blockObject))
                {
                    blockObject.TryGetPropertyValue("text", out var text);
                    texts.Add(JsSemantics.ToJsJoinElement(text));
                }
            }

            var message = JsSemantics.Trim(string.Join(" ", texts));
            return message.Length == 0 ? UnknownResultMessage : message;
        }

        return StringifyNonError(node);
    }

    private static bool IsTextBlock(JsonObject block)
        => block.TryGetPropertyValue("type", out var type)
            && type is JsonValue value
            && value.GetValueKind() == JsonValueKind.String
            && JsonTreeCopy.StringOf(value) == "text";

    private static string StringifyNonError(JsonNode node)
    {
        if (node is JsonValue)
        {
            return JsSemantics.ToJsString(node);
        }

        try
        {
            return Encoding.UTF8.GetString(JsJson.ToUtf8(node));
        }
        catch (Exception)
        {
            return JsSemantics.ToJsString(node);
        }
    }

    internal sealed class FrameInfo
    {
        internal FrameInfo(
            string? typeName,
            string? methodName,
            string? assemblyName,
            string? fileName,
            int lineNumber,
            int columnNumber)
        {
            TypeName = typeName;
            MethodName = methodName;
            AssemblyName = assemblyName;
            FileName = fileName is null || fileName.Length == 0 ? null : fileName;
            LineNumber = lineNumber;
            ColumnNumber = columnNumber;
        }

        internal string? TypeName { get; }

        internal string? MethodName { get; }

        internal string? AssemblyName { get; }

        internal string? FileName { get; }

        internal int LineNumber { get; }

        internal int ColumnNumber { get; }
    }

    internal sealed class SourceLines
    {
        internal const int MaxFileBytes = 1024 * 1024;

        private readonly Dictionary<string, string[]?> _files = new(StringComparer.Ordinal);

        internal int FileReads { get; private set; }

        internal bool TryGetLine(string path, int lineno, out string line)
        {
            line = string.Empty;
            if (lineno < 1)
            {
                return false;
            }

            if (!_files.TryGetValue(path, out var lines))
            {
                lines = Read(path);
                FileReads++;
                _files[path] = lines;
            }

            if (lines is null || lineno > lines.Length)
            {
                return false;
            }

            line = lines[lineno - 1];
            return true;
        }

        private static string[]? Read(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaxFileBytes)
                {
                    return null;
                }

                using var content = new MemoryStream((int)stream.Length);
                var chunk = new byte[8192];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (content.Length + read > MaxFileBytes)
                    {
                        return null;
                    }

                    content.Write(chunk, 0, read);
                }

                return Encoding.UTF8.GetString(content.GetBuffer(), 0, (int)content.Length).Split('\n');
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
