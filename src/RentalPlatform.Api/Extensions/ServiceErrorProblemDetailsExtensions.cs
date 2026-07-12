using Microsoft.AspNetCore.Mvc;
using RentalPlatform.Application.Common;

namespace RentalPlatform.Api.Extensions;

// Shared RFC 7807 mapping from a ServiceError to a ProblemDetails body. Each controller keeps
// its own error-code -> HTTP-status switch (the status mapping differs per feature and is
// intentionally NOT unified here); this only centralizes building the ProblemDetails object
// once the caller has already decided the status code for a given ServiceError.
public static class ServiceErrorProblemDetailsExtensions
{
    public static ProblemDetails ToProblemDetails(this ServiceError error, int statusCode)
    {
        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = error.Message,
            // RFC 7807 requires `type` to be a URI reference; the bare ServiceError code is
            // additionally exposed as the `errorCode` extension member for clients that need
            // the stable machine-readable code without parsing the urn.
            Type = $"urn:rental:error:{error.Code}"
        };

        problemDetails.Extensions["errorCode"] = error.Code;

        return problemDetails;
    }
}
