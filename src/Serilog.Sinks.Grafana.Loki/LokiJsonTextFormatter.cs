﻿// Copyright 2020-2022 Mykhailo Shevchuk & Contributors
//
// Licensed under the MIT license;
// you may not use this file except in compliance with the License.
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See LICENSE file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Serilog.Formatting.Json;
using Serilog.Parsing;
using Serilog.Sinks.Grafana.Loki.Utils;

namespace Serilog.Sinks.Grafana.Loki;

/// <summary>
/// Used to serialize a log event to a json format that loki 2.0 can parse using the json parser ( | json ), more information can be found here https://grafana.com/blog/2020/10/28/loki-2.0-released-transform-logs-as-youre-querying-them-and-set-up-alerts-within-loki/
/// </summary>
[SuppressMessage(
    "ReSharper",
    "PossibleMultipleEnumeration",
    Justification = "Reviewed")]
public class LokiJsonTextFormatter : ITextFormatter
{
    /// <summary>
    /// Renaming strategy for properties names equal to reserved keywords.
    /// </summary>
    protected readonly IReservedPropertyRenamingStrategy RenamingStrategy;

    /// <summary>
    /// <see cref="Serilog.Formatting.Display.MessageTemplateTextFormatter"/>.
    /// </summary>
    protected readonly MessageTemplateTextFormatter? MessageTemplateFormatter;

    /// <summary>
    /// <see cref="Serilog.Formatting.Json.JsonFormatter"/>.
    /// </summary>
    protected readonly JsonValueFormatter ValueFormatter;

    private static readonly string[] ReservedKeywords = { "Message", "MessageTemplate", "Renderings", "Exception" };

    /// <summary>
    /// Initializes a new instance of the <see cref="LokiJsonTextFormatter"/> class.
    /// Uses <see cref="DefaultReservedPropertyRenamingStrategy"/>.
    /// </summary>
    /// <param name="outputTemplate">
    /// The output template to use when rendering the log event's `Message` field.
    /// </param>
    public LokiJsonTextFormatter(string? outputTemplate = null)
        : this(new DefaultReservedPropertyRenamingStrategy(), outputTemplate: outputTemplate)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LokiJsonTextFormatter"/> class.
    /// </summary>
    /// <param name="renamingStrategy">
    /// Renaming strategy for properties names equal to reserved keywords.
    /// <see cref="IReservedPropertyRenamingStrategy"/>
    /// </param>
    /// <param name="outputTemplate">
    /// The output template to use when rendering the log event's `Message` field.
    /// </param>
    public LokiJsonTextFormatter(IReservedPropertyRenamingStrategy renamingStrategy, string? outputTemplate = null)
    {
        RenamingStrategy = renamingStrategy;
        MessageTemplateFormatter = outputTemplate == null ? null : new MessageTemplateTextFormatter(outputTemplate);
        ValueFormatter = new JsonValueFormatter("$type");
    }

    /// <summary>
    /// Format the log event into the output.
    /// </summary>
    /// <param name="logEvent">
    /// The event to format.
    /// </param>
    /// <param name="output">
    /// The output.
    /// </param>
    public virtual void Format(LogEvent logEvent, TextWriter output)
    {
        if (logEvent == null)
        {
            throw new ArgumentNullException(nameof(logEvent));
        }

        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        output.Write("{\"Message\":");
        if (MessageTemplateFormatter == null)
        {
            JsonValueFormatter.WriteQuotedJsonString(logEvent.RenderMessage(), output);
        }
        else
        {
          StringWriter intermediateOutput = new();
          MessageTemplateFormatter.Format(logEvent, intermediateOutput);
          JsonValueFormatter.WriteQuotedJsonString(intermediateOutput.ToString(), output);
        }

        output.Write(",\"MessageTemplate\":");
        JsonValueFormatter.WriteQuotedJsonString(logEvent.MessageTemplate.Text, output);

        var tokensWithFormat = logEvent.MessageTemplate.Tokens
            .OfType<PropertyToken>()
            .Where(pt => pt.Format != null);

        // Better not to allocate an array in the 99.9% of cases where this is false
        if (tokensWithFormat.Any())
        {
            output.Write(",\"Renderings\":[");
            var delimiter = string.Empty;
            foreach (var r in tokensWithFormat)
            {
                output.Write(delimiter);
                delimiter = ",";
                var space = new StringWriter();
                r.Render(logEvent.Properties, space);
                JsonValueFormatter.WriteQuotedJsonString(space.ToString(), output);
            }

            output.Write(']');
        }

        if (logEvent.Exception != null)
        {
            output.Write(",\"Exception\":");
            SerializeException(
                output,
                logEvent.Exception,
                1);
        }

        foreach (var (key, value) in logEvent.Properties)
        {
            var name = GetSanitizedPropertyName(key);
            output.Write(',');
            JsonValueFormatter.WriteQuotedJsonString(name, output);
            output.Write(':');
            ValueFormatter.Format(value, output);
        }

        output.Write('}');
    }

    /// <summary>
    /// Used to sanitize property name to avoid conflict with reserved keywords.
    /// Appends _ to the property name if it matches with reserved keyword.
    /// </summary>
    /// <param name="propertyName">
    /// Name of property to sanitize
    /// </param>
    protected virtual string GetSanitizedPropertyName(string propertyName) =>
        ReservedKeywords.Contains(propertyName) ? RenamingStrategy.Rename(propertyName) : propertyName;

    /// <summary>
    /// Used to serialize exceptions, can be overridden when inheriting to change the format.
    /// </summary>
    /// <param name="output">
    /// The output.
    /// </param>
    /// <param name="exception">
    /// The exception to format.
    /// </param>
    /// <param name="level">
    /// The current nesting level of the exception.
    /// </param>
    protected virtual void SerializeException(
        TextWriter output,
        Exception exception,
        int level)
    {
        if (level == 4)
        {
            JsonValueFormatter.WriteQuotedJsonString(exception.ToString(), output);

            return;
        }

        output.Write("{\"Type\":");
        var typeNamespace = exception.GetType().Namespace;
        var typeName = typeNamespace != null && typeNamespace.StartsWith("System.")
            ? exception.GetType().Name
            : exception.GetType().ToString();
        JsonValueFormatter.WriteQuotedJsonString(typeName, output);

        if (!string.IsNullOrWhiteSpace(exception.Message))
        {
            output.Write(",\"Message\":");
            JsonValueFormatter.WriteQuotedJsonString(exception.Message, output);
        }

        if (!string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            output.Write(",\"StackTrace\":");
            JsonValueFormatter.WriteQuotedJsonString(exception.StackTrace, output);
        }

        if (exception is AggregateException aggregateException)
        {
            output.Write(",\"InnerExceptions\":[");
            var count = aggregateException.InnerExceptions.Count;
            for (var i = 0; i < count; i++)
            {
                var isLast = i == count - 1;
                SerializeException(
                    output,
                    aggregateException.InnerExceptions[i],
                    level + 1);
                if (!isLast)
                {
                    output.Write(',');
                }
            }

            output.Write("]");
        }
        else if (exception.InnerException != null)
        {
            output.Write(",\"InnerException\":");
            SerializeException(
                output,
                exception.InnerException,
                level + 1);
        }

        output.Write('}');
    }
}