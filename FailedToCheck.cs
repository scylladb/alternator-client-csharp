namespace ScyllaDB.Alternator
{
    using System;

    public class FailedToCheck : Exception
    {
        public FailedToCheck(string message, Exception cause)
            : base(message, cause)
        {
        }

        public FailedToCheck(string message)
            : base(message)
        {
        }
    }
}
