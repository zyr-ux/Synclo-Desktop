using System;

namespace Synclo.Models;

public sealed class SessionExpiredException(string message = "Session expired. Please log in again.") : Exception(message);

public sealed class NetworkFailureException(string message = "Connection failed. Please check your network connection.") : Exception(message);

public sealed class ServerFailureException(string message = "Server error. Please try again later.") : Exception(message);

public sealed class InvalidCredentialsException(string message = "Incorrect email or password. Please try again.") : Exception(message);

public sealed class UserAlreadyExistsException(string message = "An account with this email address already exists.") : Exception(message);

public sealed class InvalidRequestException(string message = "Invalid request. Please check the input fields.") : Exception(message);

// NEW - Security breach detection for token reuse
public sealed class SecurityBreachException(string message = "Security verification failed. Please log in again.") : Exception(message);

// NEW - Decryption failures
public sealed class DecryptionFailedException(string message = "Failed to decrypt data. The master key might be incorrect.") : Exception(message);

// NEW - Version mismatches
public sealed class InvalidKdfVersionException(string message = "Unsupported key derivation version. Please update the application.") : Exception(message);

public sealed class InvalidBlobVersionException(string message = "Unsupported data format. Please update the application.") : Exception(message);

// NEW - Rate limit exception
public sealed class RateLimitException(string message = "Too many attempts. Please try again later.") : Exception(message);

// NEW - Device not found (deleted remotely)
public sealed class DeviceNotFoundException(string message = "Device registration not found. Please log in again.") : Exception(message);

// NEW - Genuine server verification failure
public sealed class InvalidServerException(string message = "The server is not a genuine Synclo server.") : Exception(message);
