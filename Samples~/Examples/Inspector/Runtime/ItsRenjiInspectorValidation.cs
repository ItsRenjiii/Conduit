/*
Copyright (c) 2026 ItsRenji.

Conduit example code, licensed for reuse.

Permission is granted, free of charge, to any person obtaining a copy of this file to use, copy,
modify, adapt and redistribute it, in source or compiled form, as part of their own projects and
products, including commercially, with or without attribution.

Copy it into your own folder and namespace rather than referencing it where it sits. Conduit does
not ship inside a built product, so anything pointing at this path ships broken.

This grant covers the files under Conduit's Examples folder only and does not extend to the rest
of Conduit.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;

namespace ItsRenji.Inspector
{
    /// <summary>How loudly a message about a field is presented.</summary>
    public enum MessageSeverity
    {
        /// <summary>Nothing to say.</summary>
        None,

        /// <summary>Quiet guidance, drawn like a note.</summary>
        Info,

        /// <summary>A consequence worth reading, drawn like a warning.</summary>
        Warning,

        /// <summary>Something actually wrong, drawn like an alert.</summary>
        Error,
    }

    /// <summary>
    /// What a checking method has to say about a field: whether the value passes, the sentence to show
    /// when it does not, how loudly to show it, and optionally an action that puts it right.
    /// <para>
    /// A method returning one of these exists only for the inspector, so it belongs behind
    /// <c>#if !COMPILER_UDONSHARP &amp;&amp; UNITY_EDITOR</c> on an UdonSharp behaviour. Guarded, it never
    /// enters the Udon program and can say and do whatever the editor needs.
    /// </para>
    /// </summary>
    public readonly struct ValidationResult
    {
        /// <summary>The result for a value with nothing wrong with it.</summary>
        public static ValidationResult Valid
        {
            get { return new ValidationResult(true, null, MessageSeverity.None); }
        }

        /// <summary>Builds one result.</summary>
        /// <param name="valid">Whether the value passes.</param>
        /// <param name="message">Sentence shown when it does not.</param>
        /// <param name="severity">How loudly the sentence is shown.</param>
        /// <param name="fix">Action that puts the value right, or null.</param>
        /// <param name="fixLabel">Text on the button running that action.</param>
        public ValidationResult(
            bool valid,
            string message,
            MessageSeverity severity,
            Action fix = null,
            string fixLabel = null)
        {
            IsValid = valid;
            Message = message;
            Severity = severity;
            Fix = fix;
            FixLabel = fixLabel;
        }

        /// <summary>Whether the value passes.</summary>
        public bool IsValid { get; }

        /// <summary>Sentence shown while the value does not pass.</summary>
        public string Message { get; }

        /// <summary>How loudly the sentence is shown.</summary>
        public MessageSeverity Severity { get; }

        /// <summary>Action that puts the value right, or null when there is nothing to offer.</summary>
        public Action Fix { get; }

        /// <summary>Text on the button running the fix.</summary>
        public string FixLabel { get; }

        /// <summary>Returns the same result with an action offered beside it.</summary>
        /// <param name="action">Action that puts the value right.</param>
        /// <param name="name">Text on the button, or null for the default.</param>
        /// <returns>A copy carrying the fix.</returns>
        public ValidationResult WithFix(Action action, string name = null)
        {
            return new ValidationResult(IsValid, Message, Severity, action, name ?? "Fix");
        }

        /// <summary>Builds a result that reports quiet guidance.</summary>
        /// <param name="message">Sentence to show.</param>
        /// <returns>The result.</returns>
        public static ValidationResult Info(string message)
        {
            return new ValidationResult(false, message, MessageSeverity.Info);
        }

        /// <summary>Builds a result that reports a consequence worth reading.</summary>
        /// <param name="message">Sentence to show.</param>
        /// <returns>The result.</returns>
        public static ValidationResult Warning(string message)
        {
            return new ValidationResult(false, message, MessageSeverity.Warning);
        }

        /// <summary>Builds a result that reports something actually wrong.</summary>
        /// <param name="message">Sentence to show.</param>
        /// <returns>The result.</returns>
        public static ValidationResult Error(string message)
        {
            return new ValidationResult(false, message, MessageSeverity.Error);
        }
    }
}
