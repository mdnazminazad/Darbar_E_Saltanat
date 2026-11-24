using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Application.Common;
using DarbarESaltanat.Domain.Entities;
using DarbarESaltanat.Domain.Security;
using FluentValidation;

namespace DarbarESaltanat.Application.Customers;

public sealed record CustomerInput(string Name, string Phone, string? Email = null, string? Address = null, string? Notes = null);

public sealed class CustomerInputValidator : AbstractValidator<CustomerInput>
{
    public CustomerInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Enter the customer's name").MinimumLength(2).MaximumLength(150);
        RuleFor(x => x.Phone).Must(p => CustomerService.IsValidBdMobile(CustomerService.NormalizePhone(p)))
            .WithMessage("Enter a valid Bangladeshi mobile number (01XXXXXXXXX)");
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email)).WithMessage("Invalid email");
        RuleFor(x => x.Address).MaximumLength(2000);
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}

/// <summary>Customer lookup by phone during order taking (full CRM screens arrive in Phase 4).</summary>
public sealed class CustomerService(IUnitOfWorkFactory uowFactory, ICurrentSession session, AuditFactory audit)
{
    private readonly CustomerInputValidator _validator = new();

    /// <summary>Same normalisation as the server: +880/880 prefixes, spaces and dashes removed.</summary>
    public static string NormalizePhone(string? raw)
    {
        var p = new string((raw ?? "").Where(c => !char.IsWhiteSpace(c) && c is not '-' and not '(' and not ')').ToArray());
        if (p.StartsWith("+880")) p = "0" + p[4..];
        else if (p.StartsWith("880")) p = "0" + p[3..];
        return p;
    }
    public static bool IsValidBdMobile(string p) => p.Length == 11 && p.StartsWith("01") && p[2] is >= '3' and <= '9' && p.All(char.IsDigit);

    public async Task<Customer?> FindByPhoneAsync(string phone, CancellationToken ct = default)
    {
        session.Require(Permissions.CustomersView);
        var p = NormalizePhone(phone);
        await using var uow = uowFactory.Begin();
        return (await uow.ListAsync<Customer>(c => c.Phone == p, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<Customer>> SearchAsync(string text, CancellationToken ct = default)
    {
        session.Require(Permissions.CustomersView);
        var q = text.Trim().ToLowerInvariant();
        if (q.Length < 2) return [];
        var phone = NormalizePhone(q);
        await using var uow = uowFactory.Begin();
        var rows = await uow.ListAsync<Customer>(c => c.IsActive && (c.Name.ToLower().Contains(q) || c.Phone.Contains(phone)), ct);
        return rows.OrderBy(c => c.Name).Take(20).ToList();
    }

    public async Task<Result<Customer>> CreateAsync(CustomerInput input, CancellationToken ct = default)
    {
        session.Require(Permissions.CustomersManage);
        var v = _validator.Validate(input);
        if (!v.IsValid) return Result<Customer>.Fail(v.Errors[0].ErrorMessage, "VALIDATION");
        var phone = NormalizePhone(input.Phone);
        await using var uow = uowFactory.Begin();
        if ((await uow.ListAsync<Customer>(c => c.Phone == phone, ct)).Count > 0) return Result<Customer>.Fail($"A customer with phone {phone} already exists", "DUPLICATE");
        var c = new Customer
        {
            Name = input.Name.Trim(), Phone = phone, Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim(),
            Address = string.IsNullOrWhiteSpace(input.Address) ? null : input.Address.Trim(), Notes = input.Notes?.Trim(),
        };
        uow.Add(c);
        uow.Add(audit.Create("customers.create", "customers", c.Uuid, null, new { c.Name, c.Phone }));
        await uow.CommitAsync(ct);
        return Result<Customer>.Ok(c);
    }
}
