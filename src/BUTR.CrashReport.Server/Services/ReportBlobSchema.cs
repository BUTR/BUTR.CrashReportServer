using BUTR.CrashReport.Server.Contexts;
using BUTR.CrashReport.Server.Models.Database;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using System;

namespace BUTR.CrashReport.Server.Services;

public sealed class ReportBlobSchema
{
    public sealed record BlobTable(string Table, string KeyColumn, string DictIdColumn, string DataColumn);

    public BlobTable Html { get; }
    public BlobTable Json { get; }

    public ReportBlobSchema(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        using var dbContext = dbContextFactory.CreateDbContext();
        var model = dbContext.Model;
        Html = Resolve(model, typeof(HtmlEntity));
        Json = Resolve(model, typeof(JsonEntity));
    }

    private static BlobTable Resolve(IModel model, Type clrType)
    {
        var entityType = model.FindEntityType(clrType)
            ?? throw new InvalidOperationException($"No EF entity type mapped for {clrType.Name}.");
        var store = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)
            ?? throw new InvalidOperationException($"{clrType.Name} is not mapped to a table.");
        var table = entityType.GetTableName()
            ?? throw new InvalidOperationException($"{clrType.Name} has no table name.");
        var schema = entityType.GetSchema();
        var qualifiedTable = schema is null ? Quote(table) : $"{Quote(schema)}.{Quote(table)}";

        return new BlobTable(
            qualifiedTable,
            Column(entityType, store, nameof(JsonEntity.CrashReportId)),
            Column(entityType, store, nameof(JsonEntity.DictId)),
            Column(entityType, store, nameof(JsonEntity.DataCompressed)));
    }

    private static string Column(IEntityType entityType, StoreObjectIdentifier store, string propertyName)
    {
        var property = entityType.FindProperty(propertyName)
            ?? throw new InvalidOperationException($"{entityType.ClrType.Name}.{propertyName} is not mapped.");
        var column = property.GetColumnName(store)
            ?? throw new InvalidOperationException($"{entityType.ClrType.Name}.{propertyName} has no column in {store}.");
        return Quote(column);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
