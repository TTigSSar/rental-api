namespace RentalPlatform.Application.DTOs;

/// <summary>The one uploaded file of PUT /api/auth/me/avatar, already opened by the controller.</summary>
public sealed record AvatarContent(Stream Stream, long Length);
