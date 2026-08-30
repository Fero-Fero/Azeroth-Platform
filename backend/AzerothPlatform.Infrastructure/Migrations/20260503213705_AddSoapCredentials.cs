using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzerothPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSoapCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SoapPassword",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: false,
                defaultValue: "admin");

            migrationBuilder.AddColumn<string>(
                name: "SoapUsername",
                table: "ManagedStacks",
                type: "TEXT",
                nullable: false,
                defaultValue: "admin");
                
            // Update any existing rows that have empty values
            migrationBuilder.Sql(
                @"UPDATE ManagedStacks 
                  SET SoapUsername = 'admin', SoapPassword = 'admin' 
                  WHERE SoapUsername = '' OR SoapPassword = ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SoapPassword",
                table: "ManagedStacks");

            migrationBuilder.DropColumn(
                name: "SoapUsername",
                table: "ManagedStacks");
        }
    }
}
