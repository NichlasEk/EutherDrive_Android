using System;
using System.Runtime.Serialization;

namespace Ryu64.Common
{
    public class Exceptions
    {
        public class InvalidOrUnimplementedMemoryMapException : Exception
        {
            public InvalidOrUnimplementedMemoryMapException()
                : base() { }

            public InvalidOrUnimplementedMemoryMapException(string message)
                : base(message) { }

            public InvalidOrUnimplementedMemoryMapException(string format, params object[] args)
                : base(string.Format(format, args)) { }

            public InvalidOrUnimplementedMemoryMapException(string message, Exception innerException)
                : base(message, innerException) { }

            public InvalidOrUnimplementedMemoryMapException(string format, Exception innerException, params object[] args)
                : base(string.Format(format, args), innerException) { }
        }

        public class MemoryProtectionViolation : Exception
        {
            public MemoryProtectionViolation()
                : base() { }

            public MemoryProtectionViolation(string message)
                : base(message) { }

            public MemoryProtectionViolation(string format, params object[] args)
                : base(string.Format(format, args)) { }

            public MemoryProtectionViolation(string message, Exception innerException)
                : base(message, innerException) { }

            public MemoryProtectionViolation(string format, Exception innerException, params object[] args)
                : base(string.Format(format, args), innerException) { }
        }

        public class ProgramBreakPointException : Exception
        {
            public ProgramBreakPointException()
                : base() { }

            public ProgramBreakPointException(string message)
                : base(message) { }

            public ProgramBreakPointException(string format, params object[] args)
                : base(string.Format(format, args)) { }

            public ProgramBreakPointException(string message, Exception innerException)
                : base(message, innerException) { }

            public ProgramBreakPointException(string format, Exception innerException, params object[] args)
                : base(string.Format(format, args), innerException) { }
        }

        public class InvalidOperationException : Exception
        {
            public InvalidOperationException()
                : base() { }

            public InvalidOperationException(string message)
                : base(message) { }

            public InvalidOperationException(string format, params object[] args)
                : base(string.Format(format, args)) { }

            public InvalidOperationException(string message, Exception innerException)
                : base(message, innerException) { }

            public InvalidOperationException(string format, Exception innerException, params object[] args)
                : base(string.Format(format, args), innerException) { }
        }

        public class TLBMissException : Exception
        {
            public uint Address { get; }
            public bool IsStore { get; }

            public TLBMissException()
                : base() { }

            public TLBMissException(string message)
                : base(message) { }

            public TLBMissException(string format, params object[] args)
                : base(string.Format(format, args)) { }

            public TLBMissException(string message, Exception innerException)
                : base(message, innerException) { }

            public TLBMissException(string format, Exception innerException, params object[] args)
                : base(string.Format(format, args), innerException) { }

            public TLBMissException(uint address, bool isStore = false)
                : base($"TLB miss at virtual address 0x{address:x8} (isStore={isStore})")
            {
                Address = address;
                IsStore = isStore;
            }
        }

        public class AddressErrorException : Exception
        {
            public uint Address { get; }
            public bool IsStore { get; }

            public AddressErrorException()
                : base() { }

            public AddressErrorException(string message)
                : base(message) { }

            public AddressErrorException(string format, params object[] args)
                : base(string.Format(format, args)) { }

            public AddressErrorException(string message, Exception innerException)
                : base(message, innerException) { }

            public AddressErrorException(string format, Exception innerException, params object[] args)
                : base(string.Format(format, args), innerException) { }

            public AddressErrorException(uint address, bool isStore = false)
                : base($"Address error at virtual address 0x{address:x8} (isStore={isStore})")
            {
                Address = address;
                IsStore = isStore;
            }
        }
    }
}
