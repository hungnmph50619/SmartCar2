using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartCar.Domain.Constants;
using SmartCar.Domain.Enums;
using SmartCar.Infrastructure.Persistence;
using SmartCar.Web.ViewModels;

namespace SmartCar.Web.ViewComponents;

public sealed class IndividualDocumentRenewalQueueViewComponent : ViewComponent
{
    private static readonly string[] RequiredDocumentTypes =
    {
        DocumentTypes.CitizenId,
        DocumentTypes.CitizenIdBack,
        DocumentTypes.DrivingLicense,
        DocumentTypes.DrivingLicenseBack
    };

    private readonly ApplicationDbContext _dbContext;

    public IndividualDocumentRenewalQueueViewComponent(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var pendingGroups = await _dbContext.CustomerDocuments
            .AsNoTracking()
            .Where(document =>
                document.Status == DocumentStatus.Pending &&
                RequiredDocumentTypes.Contains(document.DocumentType))
            .GroupBy(document => document.CustomerId)
            .Select(group => new
            {
                CustomerId = group.Key,
                TypeCount = group.Select(document => document.DocumentType).Distinct().Count(),
                HasCitizenFront = group.Any(document => document.DocumentType == DocumentTypes.CitizenId),
                HasCitizenBack = group.Any(document => document.DocumentType == DocumentTypes.CitizenIdBack),
                HasLicenseFront = group.Any(document => document.DocumentType == DocumentTypes.DrivingLicense),
                HasLicenseBack = group.Any(document => document.DocumentType == DocumentTypes.DrivingLicenseBack),
                SubmittedAt = group.Max(document => document.UpdatedAt)
            })
            .Where(item => item.TypeCount < RequiredDocumentTypes.Length)
            .OrderByDescending(item => item.SubmittedAt)
            .ToListAsync(HttpContext.RequestAborted);

        if (pendingGroups.Count == 0)
        {
            return View(Array.Empty<AdminKycQueueItemViewModel>());
        }

        var customerIds = pendingGroups.Select(item => item.CustomerId).ToList();
        var customers = await _dbContext.Users
            .AsNoTracking()
            .Where(user => customerIds.Contains(user.Id))
            .Select(user => new
            {
                user.Id,
                user.FullName,
                user.Email,
                user.PhoneNumber
            })
            .ToListAsync(HttpContext.RequestAborted);

        var customerById = customers.ToDictionary(item => item.Id);
        var items = pendingGroups
            .Where(item => customerById.ContainsKey(item.CustomerId))
            .Select(item =>
            {
                var customer = customerById[item.CustomerId];
                var hasCitizenPair = item.HasCitizenFront && item.HasCitizenBack;
                var hasLicensePair = item.HasLicenseFront && item.HasLicenseBack;
                var pendingText = hasCitizenPair && !hasLicensePair
                    ? "CCCD"
                    : hasLicensePair && !hasCitizenPair
                        ? "GPLX"
                        : "Giấy tờ";

                return new AdminKycQueueItemViewModel
                {
                    CustomerId = customer.Id,
                    CustomerName = customer.FullName,
                    CustomerEmail = customer.Email ?? string.Empty,
                    CustomerPhone = customer.PhoneNumber,
                    SubmittedAt = item.SubmittedAt,
                    IsFullPackage = false,
                    PendingDocumentText = pendingText
                };
            })
            .ToList();

        return View(items);
    }
}
