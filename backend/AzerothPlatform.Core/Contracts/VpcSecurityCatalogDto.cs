namespace AzerothPlatform.Core.Contracts;

/// <summary>Catalog of VPC security roles for administrators.</summary>
public class VpcSecurityCatalogDto
{
    public List<VpcSecurityRoleDto> Roles { get; set; } = new();

    public string DocumentationPath { get; set; } = "EXTERNAL-VPC-SETUP.md";
}
