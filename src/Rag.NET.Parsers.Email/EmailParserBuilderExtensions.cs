using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Parsers.Html;

namespace Rag.NET.Parsers.Email;

public static class EmailParserBuilderExtensions
{
    /// <summary>
    /// The call named in a <see cref="ParserClaim"/>, and therefore in the startup error a
    /// content-type conflict produces. It is the call a user wrote, not the private
    /// <c>Register</c> both overloads funnel through.
    /// </summary>
    private const string RegistrationMethod = "AddEmailParser()";

    /// <summary>
    /// Registers the EML (<see cref="EmailDocumentParser"/>) and MSG
    /// (<see cref="MsgDocumentParser"/>) parsers plus their shared
    /// <see cref="HtmlDocumentParser"/> dependency, with default
    /// <see cref="EmailParserOptions"/>. Both parsers receive the parser collection through a
    /// deferred <see cref="LazyDocumentParsers"/> view — they are part of the collection they
    /// dispatch attachments to, so eager constructor injection of
    /// <c>IEnumerable&lt;IDocumentParser&gt;</c> would be a circular dependency.
    /// </summary>
    public static TBuilder AddEmailParser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder =>
        Register(builder, new EmailParserOptions(), registerOptions: false);

    /// <summary>
    /// Registers both email parsers with configured <see cref="EmailParserOptions"/>, which
    /// bound how far messages embedded inside messages are followed. Calling any
    /// <c>AddEmailParser</c> overload more than once registers multiple parser instances — the
    /// pipeline uses the first whose <c>CanParse</c> matches (first registration wins at parse
    /// time), while the <see cref="EmailParserOptions"/> singleton resolved from DI is the most
    /// recently registered one (last wins); each parser instance keeps the options it was
    /// configured with.
    /// </summary>
    public static TBuilder AddEmailParser<TBuilder>(this TBuilder builder, Action<EmailParserOptions>? configure)
        where TBuilder : IRagBuilder
    {
        var options = new EmailParserOptions();
        configure?.Invoke(options);
        ValidateOptions(options, nameof(configure));
        return Register(builder, options, registerOptions: true);
    }

    private static TBuilder Register<TBuilder>(TBuilder builder, EmailParserOptions options, bool registerOptions)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<HtmlDocumentParser>();
        if (registerOptions)
            builder.Services.AddSingleton(options);

        builder.Services.AddSingleton<IDocumentParser>(sp => new EmailDocumentParser(
            new LazyDocumentParsers(sp),
            sp.GetRequiredService<HtmlDocumentParser>(),
            sp.GetService<ILogger<EmailDocumentParser>>(),
            options));
        builder.Services.AddSingleton<IDocumentParser>(sp => new MsgDocumentParser(
            new LazyDocumentParsers(sp),
            sp.GetRequiredService<HtmlDocumentParser>(),
            sp.GetService<ILogger<MsgDocumentParser>>(),
            options));

        // Declared so AddRagNet can detect another package claiming the same content type. Both
        // registrations above use factory lambdas, so ServiceDescriptor.ImplementationType is null
        // and the claim cannot be read back off the descriptor.
        builder.Services.AddSingleton(ParserClaim.For<EmailDocumentParser>(
            EmailDocumentParser.EmlContentType, RegistrationMethod));
        builder.Services.AddSingleton(ParserClaim.For<MsgDocumentParser>(
            MsgDocumentParser.MsgContentType, RegistrationMethod));
        return builder;
    }

    /// <summary>
    /// Reads <see cref="EmailParserOptions.RequestedMaxEmbeddedDepth"/> rather than
    /// <see cref="EmailParserOptions.MaxEmbeddedDepth"/>: the setter already clamped the
    /// effective value to <see cref="EmailParserOptions.MaxSupportedEmbeddedDepth"/> (that is
    /// what makes the ceiling absolute on every construction path), so the property alone can
    /// no longer be out of range and validating it would never fire. The two are equal unless
    /// the caller asked for more than the ceiling, which is exactly the case that must throw.
    /// </summary>
    private static void ValidateOptions(EmailParserOptions options, string paramName)
    {
        ThrowIfNegative(options.RequestedMaxEmbeddedDepth, nameof(EmailParserOptions.MaxEmbeddedDepth), paramName);
        ThrowIfNegative(options.MaxEmbeddedMessages, nameof(EmailParserOptions.MaxEmbeddedMessages), paramName);

        if (options.RequestedMaxEmbeddedDepth > EmailParserOptions.MaxSupportedEmbeddedDepth)
        {
            throw new ArgumentOutOfRangeException(paramName, options.RequestedMaxEmbeddedDepth,
                $"{nameof(EmailParserOptions)}.{nameof(EmailParserOptions.MaxEmbeddedDepth)} must not exceed " +
                $"{EmailParserOptions.MaxSupportedEmbeddedDepth}. The in-place traversal is flat, so the ceiling " +
                "bounds the dispatcher path — where a third-party parser registered for a message content type " +
                "re-enters through IDocumentParser, costing a bounded handful of frames per hop — plus how much " +
                "fan-out and metadata one document may ask for. (The ~500-level 0xC00000FD floor that originally " +
                "justified this ceiling belonged to the recursive traversal this replaced; it is history, not a " +
                "live bound.)");
        }
    }

    private static void ThrowIfNegative(int value, string propertyName, string paramName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value,
                $"{nameof(EmailParserOptions)}.{propertyName} must not be negative.");
        }
    }
}
