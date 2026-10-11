using Microsoft.Build.Construction;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;

namespace Meziantou.DeltaBuild;

/// <summary>
/// Evaluates the MSBuild condition provided with <c>--where</c> against evaluated projects.
/// Must be used AFTER MSBuildLocator.RegisterDefaults().
/// </summary>
internal static class ProjectCondition
{
    /// <summary>
    /// Errors that depend on the values of the properties and items of the evaluated project,
    /// such as a numeric comparison on a property that is not a number.
    /// </summary>
    private static readonly HashSet<string> ValueDependentErrorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MSB4086", // A numeric comparison was attempted on a value that is not a number
        "MSB4100", // Expected an expression to evaluate to a boolean
        "MSB4113", // The condition doesn't evaluate to a boolean
        "MSB4184", // A property function cannot be evaluated
    };

    /// <summary>
    /// Validates the syntax of the condition by evaluating it against an empty project.
    /// </summary>
    public static void Validate(string condition)
    {
        try
        {
            var projectInstance = new ProjectInstance(ProjectRootElement.Create());
            projectInstance.EvaluateCondition(condition);
        }
        catch (InvalidProjectFileException ex) when (!ValueDependentErrorCodes.Contains(ex.ErrorCode))
        {
            throw new InvalidOperationException($"The --where condition '{condition}' is invalid: {ex.BaseMessage} ({ex.ErrorCode})", ex);
        }
        catch (InvalidProjectFileException)
        {
            // The condition is well-formed but cannot be evaluated without the properties of an actual project
        }
    }

    /// <summary>
    /// Evaluates the condition using the properties and items of the project.
    /// </summary>
    public static bool Evaluate(ProjectInstance projectInstance, string condition)
    {
        try
        {
            return projectInstance.EvaluateCondition(condition);
        }
        catch (InvalidProjectFileException ex)
        {
            throw new InvalidOperationException($"The --where condition '{condition}' cannot be evaluated for project '{projectInstance.FullPath}': {ex.BaseMessage} ({ex.ErrorCode})", ex);
        }
    }
}
