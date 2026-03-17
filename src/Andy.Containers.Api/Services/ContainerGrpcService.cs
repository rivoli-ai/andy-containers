using Andy.Containers.Grpc;
using Andy.Containers.Infrastructure.Data;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Andy.Containers.Api.Services;

public class ContainerGrpcService : ContainerService.ContainerServiceBase
{
    private readonly ContainersDbContext _db;
    private readonly ITemplateValidator _validator;

    public ContainerGrpcService(ContainersDbContext db, ITemplateValidator validator)
    {
        _db = db;
        _validator = validator;
    }

    public override async Task<ValidateTemplateYamlResponse> ValidateTemplateYaml(
        ValidateTemplateYamlRequest request, ServerCallContext context)
    {
        var result = await _validator.ValidateYamlAsync(request.YamlContent, context.CancellationToken);
        var response = new ValidateTemplateYamlResponse { IsValid = result.IsValid };
        response.Errors.AddRange(result.Errors.Select(e => new ValidationError
        {
            Field = e.Field,
            Message = e.Message,
            Code = e.Severity
        }));
        response.Warnings.AddRange(result.Warnings.Select(w => new ValidationWarning
        {
            Field = w.Field,
            Message = w.Message
        }));
        return response;
    }

    public override async Task<TemplateResponse> CreateTemplateFromYaml(
        CreateTemplateFromYamlRequest request, ServerCallContext context)
    {
        var validation = await _validator.ValidateYamlAsync(request.YamlContent, context.CancellationToken);
        if (!validation.IsValid)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                string.Join("; ", validation.Errors.Select(e => $"[{e.Field}] {e.Message}"))));

        var template = await _validator.ParseYamlToTemplateAsync(request.YamlContent, context.CancellationToken);

        _db.Templates.Add(template);
        await _db.SaveChangesAsync(context.CancellationToken);

        return MapTemplate(template);
    }

    public override async Task<TemplateResponse> UpdateTemplateDefinition(
        UpdateTemplateDefinitionRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.TemplateId, out var id))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid template ID"));

        var template = await _db.Templates.FindAsync([id], context.CancellationToken);
        if (template is null)
            throw new RpcException(new Status(StatusCode.NotFound, "Template not found"));

        var validation = await _validator.ValidateYamlAsync(request.YamlContent, context.CancellationToken);
        if (!validation.IsValid)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                string.Join("; ", validation.Errors.Select(e => $"[{e.Field}] {e.Message}"))));

        var parsed = await _validator.ParseYamlToTemplateAsync(request.YamlContent, context.CancellationToken);

        template.Name = parsed.Name;
        template.Description = parsed.Description;
        template.Version = parsed.Version;
        template.BaseImage = parsed.BaseImage;
        template.IdeType = parsed.IdeType;
        template.GpuRequired = parsed.GpuRequired;
        template.GpuPreferred = parsed.GpuPreferred;
        template.Tags = parsed.Tags;
        template.DefaultResources = parsed.DefaultResources;
        template.EnvironmentVariables = parsed.EnvironmentVariables;
        template.Ports = parsed.Ports;
        template.Scripts = parsed.Scripts;
        template.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(context.CancellationToken);

        return MapTemplate(template);
    }

    public override async Task<TemplateResponse> GetTemplate(
        GetTemplateRequest request, ServerCallContext context)
    {
        Models.ContainerTemplate? template = null;

        if (!string.IsNullOrEmpty(request.TemplateId) && Guid.TryParse(request.TemplateId, out var id))
            template = await _db.Templates.FindAsync([id], context.CancellationToken);
        else if (!string.IsNullOrEmpty(request.TemplateCode))
            template = await _db.Templates.FirstOrDefaultAsync(t => t.Code == request.TemplateCode, context.CancellationToken);

        if (template is null)
            throw new RpcException(new Status(StatusCode.NotFound, "Template not found"));

        return MapTemplate(template);
    }

    public override async Task<ListTemplatesResponse> ListTemplates(
        ListTemplatesRequest request, ServerCallContext context)
    {
        var query = _db.Templates.Where(t => t.IsPublished).AsQueryable();

        if (!string.IsNullOrEmpty(request.Scope) && Enum.TryParse<Models.CatalogScope>(request.Scope, true, out var scope))
            query = query.Where(t => t.CatalogScope == scope);
        if (!string.IsNullOrEmpty(request.SearchTerm))
            query = query.Where(t => t.Name.Contains(request.SearchTerm) || (t.Description != null && t.Description.Contains(request.SearchTerm)));

        var total = await query.CountAsync(context.CancellationToken);
        var items = await query
            .OrderBy(t => t.Name)
            .Skip(request.Skip)
            .Take(request.Take > 0 ? request.Take : 20)
            .ToListAsync(context.CancellationToken);

        var response = new ListTemplatesResponse { TotalCount = total };
        response.Templates.AddRange(items.Select(MapTemplate));
        return response;
    }

    private static TemplateResponse MapTemplate(Models.ContainerTemplate t) => new()
    {
        Id = t.Id.ToString(),
        Code = t.Code,
        Name = t.Name,
        Description = t.Description ?? "",
        Version = t.Version,
        BaseImage = t.BaseImage,
        CatalogScope = t.CatalogScope.ToString(),
        IdeType = t.IdeType.ToString(),
        GpuRequired = t.GpuRequired,
        GpuPreferred = t.GpuPreferred,
        IsPublished = t.IsPublished,
        CreatedAt = t.CreatedAt.ToString("O"),
        Tags = { t.Tags ?? [] }
    };
}
