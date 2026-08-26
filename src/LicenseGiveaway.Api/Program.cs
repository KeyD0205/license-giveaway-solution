var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var allocator = new LicenseAllocator(totalLicenses: 1_000);

app.MapPost("/applications", (LicenseApplicationRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) ||
        string.IsNullOrWhiteSpace(request.Phone))
    {
        return Results.BadRequest(new
        {
            error = "Email and phone are required."
        });
    }

    var result = allocator.Apply(request.Email, request.Phone);

    return result.Status switch
    {
        ApplicationStatus.Accepted => Results.Accepted(
            $"/applications/{result.ApplicationId}",
            new
            {
                applicationId = result.ApplicationId,
                status = "Accepted",
                pdfStatus = "Generating"
            }),

        ApplicationStatus.Duplicate => Results.Conflict(new
        {
            applicationId = result.ApplicationId,
            status = "Rejected",
            reason = "Email or phone has already received a license."
        }),

        ApplicationStatus.SoldOut => Results.Json(
            new
            {
                applicationId = result.ApplicationId,
                status = "Rejected",
                reason = "All 1,000 licenses have already been allocated."
            },
            statusCode: StatusCodes.Status410Gone),

        _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
    };
});

app.MapGet("/applications/{applicationId}", (string applicationId) =>
{
    var result = allocator.Get(applicationId);

    return result is null
        ? Results.NotFound()
        : Results.Ok(result);
});

app.Run();

public record LicenseApplicationRequest(string Email, string Phone);

public enum ApplicationStatus
{
    Accepted,
    Duplicate,
    SoldOut
}

public sealed record ApplicationResult(
    string ApplicationId,
    ApplicationStatus Status);

public sealed record ApplicationView(
    string ApplicationId,
    string Email,
    string Phone,
    long Sequence,
    string Status,
    string? LicenseCode,
    string PdfStatus);

/// <summary>
/// Deliberately simple in-memory implementation for the interview exercise.
/// The lock provides atomicity for this single process only.
/// A production implementation would move the consistency boundary to a
/// transactional database and use durable queue/PDF infrastructure.
/// </summary>
public sealed class LicenseAllocator
{
    private readonly object _gate = new();
    private readonly int _totalLicenses;
    private int _allocatedLicenses;
    private long _nextSequence;

    private readonly HashSet<string> _licensedEmails = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _licensedPhones = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ApplicationView> _applications = new();

    public LicenseAllocator(int totalLicenses)
    {
        if (totalLicenses <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalLicenses));

        _totalLicenses = totalLicenses;
    }

    public ApplicationResult Apply(string email, string phone)
    {
        var normalizedEmail = NormalizeEmail(email);
        var normalizedPhone = NormalizePhone(phone);
        var applicationId = Guid.NewGuid().ToString("N");

        // Stamped before the lock so every application carries a server-observed
        // acceptance order. This is the practical definition of "first" from the
        // design doc — true client click order can't be observed by the service,
        // and .NET's lock/Monitor is not itself FIFO between waiting threads.
        var sequence = Interlocked.Increment(ref _nextSequence);

        ApplicationStatus status;
        string? licenseCode = null;

        lock (_gate)
        {
            if (_licensedEmails.Contains(normalizedEmail) ||
                _licensedPhones.Contains(normalizedPhone))
            {
                status = ApplicationStatus.Duplicate;
            }
            else if (_allocatedLicenses >= _totalLicenses)
            {
                status = ApplicationStatus.SoldOut;
            }
            else
            {
                _allocatedLicenses++;
                _licensedEmails.Add(normalizedEmail);
                _licensedPhones.Add(normalizedPhone);
                licenseCode = $"DEMO-{_allocatedLicenses:D4}";
                status = ApplicationStatus.Accepted;
            }

            _applications[applicationId] = status == ApplicationStatus.Accepted
                ? new ApplicationView(
                    applicationId, normalizedEmail, normalizedPhone, sequence,
                    "Allocated", licenseCode, "Generating")
                : new ApplicationView(
                    applicationId, normalizedEmail, normalizedPhone, sequence,
                    "Rejected", null, "NotApplicable");
        }

        // Stub dispatched after releasing the lock: a real implementation enqueues a
        // durable PDF-generation job here. Keeping it outside the critical section
        // means a slow or failing PDF job never serializes behind license allocation.
        if (status == ApplicationStatus.Accepted)
        {
            GeneratePdfStub(applicationId, licenseCode!);
        }

        return new(applicationId, status);
    }

    public ApplicationView? Get(string applicationId)
    {
        lock (_gate)
        {
            return _applications.TryGetValue(applicationId, out var application)
                ? application
                : null;
        }
    }

    private static string NormalizeEmail(string email) =>
        email.Trim().ToLowerInvariant();

    private static string NormalizePhone(string phone) =>
        new string(phone.Where(char.IsDigit).ToArray());

    private static void GeneratePdfStub(string applicationId, string licenseCode)
    {
        Console.WriteLine(
            $"[PDF STUB] Generate certificate for {applicationId}, license {licenseCode}");
    }
}
