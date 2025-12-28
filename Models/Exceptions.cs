using System;

namespace Synclo.Models;

public sealed class SessionExpiredException() : Exception("Session expired.");

public sealed class NetworkFailureException() : Exception("Network failure.");

public sealed class ServerFailureException(string message) : Exception(message);

public sealed class InvalidCredentialsException(string message) : Exception(message);

public sealed class UserAlreadyExistsException(string message) : Exception(message);

public sealed class InvalidRequestException(string message) : Exception(message);

// NEW - Security breach detection for token reuse
public sealed class SecurityBreachException(string message) : Exception(message);

// NEW - Decryption failures
public sealed class DecryptionFailedException(string message) : Exception(message);

// NEW - Version mismatches
public sealed class InvalidKdfVersionException(string message) : Exception(message);

public sealed class InvalidBlobVersionException(string message) : Exception(message);

