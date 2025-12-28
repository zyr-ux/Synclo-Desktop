using System;

namespace Synclo.Models;

public sealed class SessionExpiredException() : Exception("Session expired.");

public sealed class NetworkFailureException() : Exception("Network failure.");

public sealed class ServerFailureException(string message) : Exception(message);

public sealed class InvalidCredentialsException(string message) : Exception(message);

public sealed class UserAlreadyExistsException(string message) : Exception(message);

public sealed class InvalidRequestException(string message) : Exception(message);