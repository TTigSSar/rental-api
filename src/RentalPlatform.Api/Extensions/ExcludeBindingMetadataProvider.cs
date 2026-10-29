using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace RentalPlatform.Api.Extensions;

// Marks one named property of one type as never bindable (the equivalent of [BindNever], which the
// Application layer cannot reference without an ASP.NET dependency).
public sealed class PropertyBindingExclusion : IBindingMetadataProvider
{
    private readonly Type _containerType;
    private readonly string _propertyName;

    public PropertyBindingExclusion(Type containerType, string propertyName)
    {
        _containerType = containerType;
        _propertyName = propertyName;
    }

    public void CreateBindingMetadata(BindingMetadataProviderContext context)
    {
        var key = context.Key;
        if (key.MetadataKind == ModelMetadataKind.Property
            && key.ContainerType == _containerType
            && key.Name == _propertyName)
        {
            context.BindingMetadata.IsBindingAllowed = false;
        }
    }
}
