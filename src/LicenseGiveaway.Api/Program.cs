using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Placeholder-file persistence, per the interviewer's note that an actual
// database isn't required. One JSON line is appended per resolved
// application; a real implementation would make this write part of the same
// transaction as allocation (e.g. a DB row), not a best-effort side effect.
// Guarded by its own lock since concurrent writers would otherwise race on
// the file handle — kept separate from the allocator's allocation lock so a
// slow/blocked write can never serialize behind license allocation itself.
var applicationsFilePath = Path.Combine(AppContext.BaseDirectory, "applications.jsonl");
var fileLock = new object();

var allocator = new LicenseAllocator(
    totalLicenses: 1_000,
    persist: view =>
    {
        var json = JsonSerializer.Serialize(view);
        lock (fileLock)
        {
            File.AppendAllText(applicationsFilePath, json + Environment.NewLine);
        }
    });

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
    private readonly Action<ApplicationView> _persist;

    private readonly HashSet<string> _licensedEmails = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _licensedPhones = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ApplicationView> _applications = new();

    /// <param name="persist">
    /// Called with the resolved record for every application, outside the
    /// allocation lock. Defaults to a no-op so unit tests stay in-memory only
    /// and don't touch disk; Program.cs wires a real placeholder-file writer.
    /// </param>
    public LicenseAllocator(int totalLicenses, Action<ApplicationView>? persist = null)
    {
        if (totalLicenses <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalLicenses));

        _totalLicenses = totalLicenses;
        _persist = persist ?? (_ => { });
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
        ApplicationView view;

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

            view = status == ApplicationStatus.Accepted
                ? new ApplicationView(
                    applicationId, normalizedEmail, normalizedPhone, sequence,
                    "Allocated", licenseCode, "Generating")
                : new ApplicationView(
                    applicationId, normalizedEmail, normalizedPhone, sequence,
                    "Rejected", null, "NotApplicable");

            _applications[applicationId] = view;
        }

        // Both dispatched after releasing the lock: a real implementation would make
        // the persistence write part of the same transaction as allocation (a DB row),
        // and enqueue a durable PDF-generation job. Keeping both outside the critical
        // section means a slow write or failing PDF job never serializes behind or
        // jeopardizes license allocation itself.
        _persist(view);

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
