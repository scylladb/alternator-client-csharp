namespace ScyllaDB.Alternator
{
    using System;

    public class FailedToCheck : Exception
    {
        public FailedToCheck()
        {
        }

        public FailedToCheck(string? message)
            : base(message)
        {
        }

        public FailedToCheck(string? message, Exception? innerException)
            : base(message, innerException)
        {
        }
    }
}
