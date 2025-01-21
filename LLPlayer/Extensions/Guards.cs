using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace LLPlayer.Extensions;

public static class Guards
{
    /// <summary>Throws an <see cref="InvalidOperationException"/> immediately.</summary>
    /// <param name="message">error message</param>
    /// <exception cref="InvalidOperationException"></exception>
    public static void Fail(string? message = null)
    {
        throw new InvalidOperationException(message);
    }

    /// <summary>Throws an <see cref="InvalidOperationException"/> if <paramref name="variable"/> is null.</summary>
    /// <param name="variable">The reference type variable to validate as non-null.</param>
    /// <param name="variableName">The name of the variable with which <paramref name="variable"/> corresponds.</param>
    /// <exception cref="InvalidOperationException"></exception>
    public static void ThrowIfNull([NotNull] object? variable, [CallerArgumentExpression(nameof(variable))] string? variableName = null)
    {
        if (variable is null)
        {
            throw new InvalidOperationException(variableName);
        }
    }
}
