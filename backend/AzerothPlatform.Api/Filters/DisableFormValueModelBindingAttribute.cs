using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AzerothPlatform.Api.Filters;

/// <summary>
/// Strips the form value providers from an action so ASP.NET does not bind (and therefore does not
/// buffer) the request body before the action runs.
///
/// Required for the client upload endpoints. With the default binders an <c>IFormFile</c> parameter is
/// materialised before the first line of the action executes, which spools the entire body to the
/// server's temp directory first: a 17 GB base client lands on manager disk once for ASP.NET, again
/// for our own staging copy, and the "an operation is already running" guard cannot reject the request
/// until all of that has happened. Actions marked with this attribute read the body themselves with a
/// <c>MultipartReader</c> instead.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        factories.RemoveType<FormValueProviderFactory>();
        factories.RemoveType<FormFileValueProviderFactory>();
        factories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
