using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// An <see cref="ILogger"/> that keeps everything in a list so tests can
    /// assert on what was logged.
    ///
    /// Implements the interface directly rather than deriving from the API's
    /// own <c>LoggerBase</c>. That base class runs a static constructor which
    /// throws a sacrificial exception and reads a filename out of the stack
    /// trace — which returns null, and NullReferenceExceptions, whenever the
    /// API assembly has no debug symbols beside it. A normal game install has
    /// none, so deriving from it would make this harness fail on exactly the
    /// machines it is meant to run on.
    /// </summary>
    public sealed class CollectingLogger : ILogger
    {
        public readonly List<string> Lines = new List<string>();

        public bool TraceLog { get; set; }

#pragma warning disable 67 // Never raised; present to satisfy the interface.
        public event LogEntryDelegate EntryAdded;
#pragma warning restore 67

        public void ClearWatchers() { }

        public bool Contains(string fragment) => Lines.Exists(l => l.Contains(fragment));

        public void Log(EnumLogType logType, string format, params object[] args)
        {
            // Callers pass pre-built messages with no args; only format when
            // there are args, or a stray brace in injected GLSL would throw.
            Lines.Add(logType + ": " + (args != null && args.Length > 0 ? string.Format(format, args) : format));
        }

        public void Log(EnumLogType logType, string message) => Log(logType, message, null);

        public void LogException(EnumLogType logType, Exception e) => Log(logType, e.ToString());

        public void Chat(string format, params object[] args) => Log(EnumLogType.Chat, format, args);
        public void Chat(string message) => Log(EnumLogType.Chat, message);
        public void Event(string format, params object[] args) => Log(EnumLogType.Event, format, args);
        public void Event(string message) => Log(EnumLogType.Event, message);
        public void StoryEvent(string format, params object[] args) => Log(EnumLogType.StoryEvent, format, args);
        public void StoryEvent(string message) => Log(EnumLogType.StoryEvent, message);
        public void Build(string format, params object[] args) => Log(EnumLogType.Build, format, args);
        public void Build(string message) => Log(EnumLogType.Build, message);
        public void VerboseDebug(string format, params object[] args) => Log(EnumLogType.VerboseDebug, format, args);
        public void VerboseDebug(string message) => Log(EnumLogType.VerboseDebug, message);
        public void Debug(string format, params object[] args) => Log(EnumLogType.Debug, format, args);
        public void Debug(string message) => Log(EnumLogType.Debug, message);
        public void Notification(string format, params object[] args) => Log(EnumLogType.Notification, format, args);
        public void Notification(string message) => Log(EnumLogType.Notification, message);
        public void Warning(string format, params object[] args) => Log(EnumLogType.Warning, format, args);
        public void Warning(string message) => Log(EnumLogType.Warning, message);
        public void Warning(Exception e) => Log(EnumLogType.Warning, e.ToString());
        public void Error(string format, params object[] args) => Log(EnumLogType.Error, format, args);
        public void Error(string message) => Log(EnumLogType.Error, message);
        public void Error(Exception e) => Log(EnumLogType.Error, e.ToString());
        public void Fatal(string format, params object[] args) => Log(EnumLogType.Fatal, format, args);
        public void Fatal(string message) => Log(EnumLogType.Fatal, message);
        public void Fatal(Exception e) => Log(EnumLogType.Fatal, e.ToString());
        public void Audit(string format, params object[] args) => Log(EnumLogType.Audit, format, args);
        public void Audit(string message) => Log(EnumLogType.Audit, message);
    }
}
