"""Custom exceptions for G300s operations."""

class G300sError(Exception):
    """Base exception for all G300s errors."""
    pass

class G300sDeviceNotFoundError(G300sError):
    """Raised when the G300s mouse cannot be opened or is not plugged in."""
    pass

class G300sWriteTimeoutError(G300sError):
    """Raised when a profile write did not settle in flash within the timeout."""
    pass

class G300sBusyError(G300sError):
    """Raised when the mouse flash controller remains busy and rejects writes."""
    pass

class G300sValidationError(G300sError):
    """Raised when input parameters (DPI, buttons, rates) are out of bounds."""
    pass

class G300sConflictWarning(UserWarning):
    """Warning emitted when conflicting vendor software (e.g. LCore.exe) is detected."""
    pass
