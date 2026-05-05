// license:BSD-3-Clause
// copyright-holders:Edward Fast

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

using s32 = System.Int32;
using u8 = System.Byte;
using u32 = System.UInt32;


namespace mame
{
    // callback delegate for presave/postload
    //typedef named_delegate<void ()> save_prepost_delegate;
    public delegate void save_prepost_delegate();


    public class save_manager
    {
        //friend class ram_state;
        //friend class rewinder;


        class state_entry
        {
            public readonly object m_data;
            public readonly string m_name;
            public readonly device_t m_device;
            public readonly string m_module;
            public readonly string m_tag;
            public readonly int m_index;
            public readonly u8 m_typesize;
            public readonly u32 m_typecount;
            public readonly u32 m_blockcount;
            public readonly u32 m_stride;


            // construction/destruction
            //-------------------------------------------------
            //  state_entry - constructor
            //-------------------------------------------------
            public state_entry(object data, string name, device_t device, string module, string tag, int index, u8 size, u32 valcount, u32 blockcount, u32 stride)
            {
                m_data = data;
                m_device = device;
                m_module = module ?? "global";
                m_tag = tag ?? device?.tag() ?? "";
                m_index = index;
                m_typesize = size;
                m_typecount = valcount;
                m_blockcount = blockcount;
                m_stride = stride;
                m_name = $"{m_module}:{m_tag}:{m_index}:{name}";
            }


            // helpers
            //void flip_data();
        }

        class state_accessor
        {
            readonly Func<object> m_getter;
            readonly Action<object> m_setter;

            public state_accessor(Type type, Func<object> getter, Action<object> setter)
            {
                Type = type;
                m_getter = getter;
                m_setter = setter;
            }

            public Type Type { get; }
            public object Get() { return m_getter(); }
            public void Set(object value) { m_setter(value); }
        }


        // internal state
        running_machine m_machine;              // reference to our machine
        //rewinder m_rewind;               // rewinder
        bool m_reg_allowed;          // are registrations allowed?
        s32 m_illegal_regs;         // number of illegal registrations

        List<state_entry> m_entry_list = new List<state_entry>();  //std::vector<std::unique_ptr<state_entry>>    m_entry_list;       // list of registered entries
        //std::vector<std::unique_ptr<ram_state>>      m_ramstate_list;    // list of ram states
        List<save_prepost_delegate> m_presave_list = new List<save_prepost_delegate>();
        List<save_prepost_delegate> m_postload_list = new List<save_prepost_delegate>();


        // construction/destruction

        //-------------------------------------------------
        //  save_manager - constructor
        //-------------------------------------------------
        public save_manager(running_machine machine)
        {
            m_machine = machine;
            m_reg_allowed = true;
            m_illegal_regs = 0;
        }


        // getters
        //running_machine &machine() const { return m_machine; }
        //rewinder rewind() { return m_rewind.get(); }
        public int registration_count() { return m_entry_list.Count; }
        public bool registration_allowed() { return m_reg_allowed; }


        // registration control
        public void allow_registration(bool allowed = true)
        {
            m_reg_allowed = allowed;
        }

        //const char *indexed_item(int index, void *&base, u32 &valsize, u32 &valcount, u32 &blockcount, u32 &stride) const;


        // function registration
        //-------------------------------------------------
        //  register_presave - register a pre-save
        //  function callback
        //-------------------------------------------------
        public void register_presave(save_prepost_delegate func)
        {
            // check for invalid timing
            if (!m_reg_allowed)
                throw new emu_fatalerror("Attempt to register callback function after state registration is closed!\n");

            // scan for duplicates and push through to the end
            if (m_presave_list.Contains(func))
                throw new emu_fatalerror("Duplicate save state pre-save function\n");

            // allocate a new entry
            m_presave_list.Add(func);
        }

        //-------------------------------------------------
        //  state_save_register_postload -
        //  register a post-load function callback
        //-------------------------------------------------
        public void register_postload(save_prepost_delegate func)
        {
            // check for invalid timing
            if (!m_reg_allowed)
                throw new emu_fatalerror("Attempt to register callback function after state registration is closed!\n");

            // scan for duplicates and push through to the end
            if (m_postload_list.Contains(func))
                throw new emu_fatalerror("Duplicate save state post-load function\n");

            // allocate a new entry
            m_postload_list.Add(func);
        }


        // callback dispatching
        void dispatch_presave()
        {
            foreach (save_prepost_delegate callback in m_presave_list)
                callback();
        }

        void dispatch_postload()
        {
            foreach (save_prepost_delegate callback in m_postload_list)
                callback();
        }


        // generic memory registration
        public void save_memory(device_t device, string module, string tag, int index, string name, object val, u8 valsize, u32 valcount = 1, u32 blockcount = 1, u32 stride = 0)
        {
            register_entry(new state_entry(val, name, device, module, tag, index, valsize, valcount, blockcount, stride));
        }

        public void save_item_ref<ItemType>(device_t device, string module, string tag, int index, string name, Func<ItemType> getter, Action<ItemType> setter)
        {
            var accessor = new state_accessor(
                typeof(ItemType),
                () => getter(),
                value => setter((ItemType)value));
            save_memory(device, module, tag, index, name, accessor, (u8)ElementSize(default(ItemType)), 1);
        }


        // templatized wrapper for general objects and arrays
        //template <typename ItemType>
        public void save_item<ItemType>(device_t device, string module, string tag, int index, ItemType value, string valname)  //void save_item(device_t *device, const char *module, const char *tag, int index, ItemType &value, const char *valname)
        {
            object boxed = value;
            save_memory(device, module, tag, index, valname, boxed, (u8)ElementSize(boxed), (u32)ElementCount(boxed));
        }

        public void save_item<ItemType>(device_t device, string module, string tag, int index, Tuple<ItemType, string> value)
        { save_item(device, module, tag, index, value.Item1, value.Item2); }


        // templatized wrapper for structure members
        //template <typename ItemType, typename StructType, typename ElementType>
        //void save_item(device_t *device, const char *module, const char *tag, int index, ItemType &value, ElementType StructType::*element, const char *valname)
        //{
        //    static_assert(std::is_base_of<StructType, typename array_unwrap<ItemType>::underlying_type>::value, "Called save_item on a non-matching struct member pointer!");
        //    static_assert(!(sizeof(typename array_unwrap<ItemType>::underlying_type) % sizeof(typename array_unwrap<ElementType>::underlying_type)), "Called save_item on an unaligned struct member!");
        //    static_assert(!type_checker<ElementType>::is_pointer, "Called save_item on a struct member pointer!");
        //    static_assert(type_checker<typename array_unwrap<ElementType>::underlying_type>::is_atom, "Called save_item on a non-fundamental type!");
        //    save_memory(device, module, tag, index, valname, array_unwrap<ElementType>::ptr(array_unwrap<ItemType>::ptr(value)->*element), array_unwrap<ElementType>::SIZE, array_unwrap<ElementType>::SAVE_COUNT, array_unwrap<ItemType>::SAVE_COUNT, sizeof(typename array_unwrap<ItemType>::underlying_type) / sizeof(typename array_unwrap<ElementType>::underlying_type));
        //}

        // templatized wrapper for pointers
        //template <typename ItemType>
        //void save_pointer(device_t *device, const char *module, const char *tag, int index, ItemType *value, const char *valname, u32 count)
        //{
        //    static_assert(type_checker<typename array_unwrap<ItemType>::underlying_type>::is_atom, "Called save_pointer on a non-fundamental type!");
        //    save_memory(device, module, tag, index, valname, array_unwrap<ItemType>::ptr(value[0]), array_unwrap<ItemType>::SIZE, array_unwrap<ItemType>::SAVE_COUNT * count);
        //}

        //template <typename ItemType, typename StructType, typename ElementType>
        //void save_pointer(device_t *device, const char *module, const char *tag, int index, ItemType *value, ElementType StructType::*element, const char *valname, u32 count)
        //{
        //    static_assert(std::is_base_of<StructType, typename array_unwrap<ItemType>::underlying_type>::value, "Called save_pointer on a non-matching struct member pointer!");
        //    static_assert(!(sizeof(typename array_unwrap<ItemType>::underlying_type) % sizeof(typename array_unwrap<ElementType>::underlying_type)), "Called save_pointer on an unaligned struct member!");
        //    static_assert(!type_checker<ElementType>::is_pointer, "Called save_pointer on a struct member pointer!");
        //    static_assert(type_checker<typename array_unwrap<ElementType>::underlying_type>::is_atom, "Called save_pointer on a non-fundamental type!");
        //    save_memory(device, module, tag, index, valname, array_unwrap<ElementType>::ptr(array_unwrap<ItemType>::ptr(value[0])->*element), array_unwrap<ElementType>::SIZE, array_unwrap<ElementType>::SAVE_COUNT, array_unwrap<ItemType>::SAVE_COUNT * count, sizeof(typename array_unwrap<ItemType>::underlying_type) / sizeof(typename array_unwrap<ElementType>::underlying_type));
        //}

        // templatized wrapper for std::unique_ptr
        //template <typename ItemType>
        //void save_pointer(device_t *device, const char *module, const char *tag, int index, const std::unique_ptr<ItemType []> &value, const char *valname, u32 count)
        //{
        //    static_assert(type_checker<typename array_unwrap<ItemType>::underlying_type>::is_atom, "Called save_pointer on a non-fundamental type!");
        //    save_memory(device, module, tag, index, valname, array_unwrap<ItemType>::ptr(value[0]), array_unwrap<ItemType>::SIZE, array_unwrap<ItemType>::SAVE_COUNT * count);
        //}

        //template <typename ItemType, typename StructType, typename ElementType>
        //void save_pointer(device_t *device, const char *module, const char *tag, int index, const std::unique_ptr<ItemType []> &value, ElementType StructType::*element, const char *valname, u32 count)
        //{
        //    static_assert(std::is_base_of<StructType, typename array_unwrap<ItemType>::underlying_type>::value, "Called save_pointer on a non-matching struct member pointer!");
        //    static_assert(!(sizeof(typename array_unwrap<ItemType>::underlying_type) % sizeof(typename array_unwrap<ElementType>::underlying_type)), "Called save_pointer on an unaligned struct member!");
        //    static_assert(!type_checker<ElementType>::is_pointer, "Called save_pointer on a struct member pointer!");
        //    static_assert(type_checker<typename array_unwrap<ElementType>::underlying_type>::is_atom, "Called save_pointer on a non-fundamental type!");
        //    save_memory(device, module, tag, index, valname, array_unwrap<ElementType>::ptr(array_unwrap<ItemType>::ptr(value[0])->*element), array_unwrap<ElementType>::SIZE, array_unwrap<ElementType>::SAVE_COUNT, array_unwrap<ItemType>::SAVE_COUNT * count, sizeof(typename array_unwrap<ItemType>::underlying_type) / sizeof(typename array_unwrap<ElementType>::underlying_type));
        //}


        // global memory registration
        //template<typename ItemType>
        public void save_item<ItemType>(ItemType value, string valname, int index = 0)
        { save_item(null, "global", null, index, value, valname); }

        // state saving interfaces
        //template<typename _ItemType>
        public void save_item<ItemType>(Tuple<ItemType, string> value, int index = 0)
        { save_item(null, "global", null, index, value.Item1, value.Item2); }

        //template <typename ItemType, typename StructType, typename ElementType>
        //void save_item(ItemType &value, ElementType StructType::*element, const char *valname, int index = 0)
        //{ save_item(nullptr, "global", nullptr, index, value, element, valname); }

        //template <typename ItemType>
        //void save_pointer(ItemType &&value, const char *valname, u32 count, int index = 0)
        //{ save_pointer(nullptr, "global", nullptr, index, std::forward<ItemType>(value), valname, count); }
        //template <typename ItemType, typename StructType, typename ElementType>
        //void save_pointer(ItemType &&value, ElementType StructType::*element, const char *valname, u32 count, int index = 0)
        //{ save_pointer(nullptr, "global", nullptr, index, std::forward<ItemType>(value), element, valname, count); }


        // file processing
        //static save_error check_file(running_machine &machine, util::core_file &file, const char *gamename, void (CLIB_DECL *errormsg)(const char *fmt, ...));
        //save_error write_file(util::core_file &file);
        //save_error read_file(util::core_file &file);

        //save_error write_stream(std::ostream &str);
        //save_error read_stream(std::istream &str);
        public void write_stream(BinaryWriter writer)
        {
            dispatch_presave();

            writer.Write(Encoding.ASCII.GetBytes("MCSSTATE"));
            writer.Write(1);
            writer.Write(m_entry_list.Count);

            foreach (state_entry entry in m_entry_list)
            {
                writer.Write(entry.m_name);

                using MemoryStream payload = new MemoryStream();
                using (BinaryWriter payloadWriter = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
                    WriteEntryPayload(payloadWriter, entry);

                writer.Write((int)payload.Length);
                writer.Write(payload.ToArray());
            }
        }

        public void read_stream(BinaryReader reader)
        {
            string magic = Encoding.ASCII.GetString(reader.ReadBytes(8));
            if (magic != "MCSSTATE")
                throw new InvalidDataException("MCS savestate magic mismatch.");

            int version = reader.ReadInt32();
            if (version != 1)
                throw new InvalidDataException($"MCS savestate version mismatch: {version}.");

            Dictionary<string, state_entry> entries = new Dictionary<string, state_entry>();
            foreach (state_entry entry in m_entry_list)
                entries[entry.m_name] = entry;

            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                int length = reader.ReadInt32();
                byte[] payload = reader.ReadBytes(length);
                if (payload.Length != length)
                    throw new EndOfStreamException("Unexpected end of MCS savestate payload.");

                if (entries.TryGetValue(name, out state_entry entry))
                {
                    using MemoryStream payloadStream = new MemoryStream(payload);
                    using BinaryReader payloadReader = new BinaryReader(payloadStream, Encoding.UTF8, leaveOpen: false);
                    ReadEntryPayload(payloadReader, entry);
                }
            }

            dispatch_postload();
        }

        //save_error write_buffer(void *buf, size_t size);
        //save_error read_buffer(const void *buf, size_t size);


        // internal helpers
        //template <typename T, typename U, typename V, typename W>
        //save_error do_write(T check_space, U write_block, V start_header, W start_data);
        //template <typename T, typename U, typename V, typename W>
        //save_error do_read(T check_length, U read_block, V start_header, W start_data);
        //u32 signature() const;
        //void dump_registry() const;
        //static save_error validate_header(const u8 *header, const char *gamename, u32 signature, void (CLIB_DECL *errormsg)(const char *fmt, ...), const char *error_prefix);

        void register_entry(state_entry entry)
        {
            if (!m_reg_allowed)
            {
                m_illegal_regs++;
                throw new emu_fatalerror("Attempt to register save state item after state registration is closed: {0}\n", entry.m_name);
            }

            m_entry_list.Add(entry);
        }

        static int ElementSize(object value)
        {
            Type type = UnwrapContainerElementType(value) ?? value?.GetType() ?? typeof(byte);
            if (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte)) return 1;
            if (type == typeof(short) || type == typeof(ushort)) return 2;
            if (type == typeof(int) || type == typeof(uint) || type == typeof(float) || type == typeof(rgb_t)) return 4;
            if (type == typeof(long) || type == typeof(ulong) || type == typeof(double)) return 8;
            if (type == typeof(attotime)) return 16;
            return 0;
        }

        static int ElementCount(object value)
        {
            if (value is Array array)
                return array.Length;
            if (value is IList list)
                return list.Count;
            return 1;
        }

        static Type UnwrapContainerElementType(object value)
        {
            if (value == null)
                return null;

            Type type = value.GetType();
            if (type.IsArray)
                return type.GetElementType();

            while (type != null)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(MemoryContainer<>))
                    return type.GetGenericArguments()[0];
                type = type.BaseType;
            }

            return null;
        }

        static void WriteEntryPayload(BinaryWriter writer, state_entry entry)
        {
            object value = entry.m_data;
            if (value is state_accessor accessor)
            {
                Type type = accessor.Type;
                writer.Write((byte)'r');
                writer.Write(type.AssemblyQualifiedName ?? type.FullName ?? "");
                WritePrimitive(writer, type, accessor.Get());
            }
            else if (value is intref intRef)
            {
                writer.Write((byte)'i');
                writer.Write(intRef.i);
            }
            else if (value is doubleref doubleRef)
            {
                writer.Write((byte)'d');
                writer.Write(doubleRef.d);
            }
            else if (value is Array array && IsSupportedElementType(array.GetType().GetElementType()))
            {
                Type arrayElementType = array.GetType().GetElementType();
                writer.Write((byte)'a');
                writer.Write(arrayElementType.AssemblyQualifiedName ?? arrayElementType.FullName ?? "");
                writer.Write(array.Rank);
                for (int rank = 0; rank < array.Rank; rank++)
                    writer.Write(array.GetLength(rank));
                writer.Write(array.Length);
                foreach (object item in array)
                    WritePrimitive(writer, arrayElementType, item);
            }
            else if (value is IList list && UnwrapContainerElementType(value) is Type elementType && IsSupportedElementType(elementType))
            {
                writer.Write((byte)'l');
                writer.Write(elementType.AssemblyQualifiedName ?? elementType.FullName ?? "");
                writer.Write(list.Count);
                for (int i = 0; i < list.Count; i++)
                    WritePrimitive(writer, elementType, list[i]);
            }
            else if (IsSupportedElementType(value?.GetType()))
            {
                Type type = value.GetType();
                writer.Write((byte)'s');
                writer.Write(type.AssemblyQualifiedName ?? type.FullName ?? "");
                WritePrimitive(writer, type, value);
            }
            else
            {
                writer.Write((byte)'n');
            }
        }

        static void ReadEntryPayload(BinaryReader reader, state_entry entry)
        {
            byte kind = reader.ReadByte();
            object value = entry.m_data;
            if (kind == (byte)'r' && value is state_accessor accessor)
            {
                Type type = ResolveType(reader.ReadString());
                accessor.Set(ReadPrimitive(reader, type));
            }
            else if (kind == (byte)'i' && value is intref intRef)
            {
                intRef.i = reader.ReadInt32();
            }
            else if (kind == (byte)'d' && value is doubleref doubleRef)
            {
                doubleRef.d = reader.ReadDouble();
            }
            else if (kind == (byte)'l' && value is IList list)
            {
                Type elementType = ResolveType(reader.ReadString());
                int count = reader.ReadInt32();
                if (list is MemoryContainer<byte> bytes)
                {
                    if (bytes.Count != count)
                        bytes.Resize(count);
                    for (int i = 0; i < count; i++)
                        bytes[i] = reader.ReadByte();
                    return;
                }

                int targetCount = Math.Min(count, list.Count);
                for (int i = 0; i < count; i++)
                {
                    object item = ReadPrimitive(reader, elementType);
                    if (i < targetCount)
                        list[i] = item;
                }
            }
            else if (kind == (byte)'a' && value is Array array)
            {
                Type elementType = ResolveType(reader.ReadString());
                int rank = reader.ReadInt32();
                int[] lengths = new int[rank];
                for (int axis = 0; axis < rank; axis++)
                    lengths[axis] = reader.ReadInt32();
                int count = reader.ReadInt32();
                int targetCount = Math.Min(count, array.Length);
                for (int i = 0; i < count; i++)
                {
                    object item = ReadPrimitive(reader, elementType);
                    if (i < targetCount)
                        SetArrayLinearValue(array, i, item);
                }
            }
            else if (kind == (byte)'s')
            {
                Type type = ResolveType(reader.ReadString());
                _ = ReadPrimitive(reader, type);
                // Boxed value types registered through the C# NAME shim are copies, not references.
                // Drivers must use reference/delegate registration for scalar state that needs restore.
            }
        }

        static bool IsSupportedElementType(Type type)
        {
            return type == typeof(bool) ||
                type == typeof(byte) || type == typeof(sbyte) ||
                type == typeof(short) || type == typeof(ushort) ||
                type == typeof(int) || type == typeof(uint) ||
                type == typeof(long) || type == typeof(ulong) ||
                type == typeof(float) || type == typeof(double) ||
                type == typeof(rgb_t) ||
                type == typeof(attotime);
        }

        static Type ResolveType(string typeName)
        {
            return Type.GetType(typeName) ?? typeof(byte);
        }

        static void WritePrimitive(BinaryWriter writer, Type type, object value)
        {
            if (type == typeof(bool)) writer.Write(value != null && (bool)value);
            else if (type == typeof(byte)) writer.Write(value != null ? (byte)value : (byte)0);
            else if (type == typeof(sbyte)) writer.Write(value != null ? (sbyte)value : (sbyte)0);
            else if (type == typeof(short)) writer.Write(value != null ? (short)value : (short)0);
            else if (type == typeof(ushort)) writer.Write(value != null ? (ushort)value : (ushort)0);
            else if (type == typeof(int)) writer.Write(value != null ? (int)value : 0);
            else if (type == typeof(uint)) writer.Write(value != null ? (uint)value : 0U);
            else if (type == typeof(rgb_t)) writer.Write(value != null ? (uint)(rgb_t)value : 0U);
            else if (type == typeof(attotime))
            {
                attotime time = value != null ? (attotime)value : attotime.zero;
                writer.Write(time.m_seconds);
                writer.Write(time.m_attoseconds);
            }
            else if (type == typeof(long)) writer.Write(value != null ? (long)value : 0L);
            else if (type == typeof(ulong)) writer.Write(value != null ? (ulong)value : 0UL);
            else if (type == typeof(float)) writer.Write(value != null ? (float)value : 0.0f);
            else if (type == typeof(double)) writer.Write(value != null ? (double)value : 0.0);
            else throw new InvalidDataException($"Unsupported MCS savestate primitive type: {type.FullName}");
        }

        static object ReadPrimitive(BinaryReader reader, Type type)
        {
            if (type == typeof(bool)) return reader.ReadBoolean();
            if (type == typeof(byte)) return reader.ReadByte();
            if (type == typeof(sbyte)) return reader.ReadSByte();
            if (type == typeof(short)) return reader.ReadInt16();
            if (type == typeof(ushort)) return reader.ReadUInt16();
            if (type == typeof(int)) return reader.ReadInt32();
            if (type == typeof(uint)) return reader.ReadUInt32();
            if (type == typeof(rgb_t)) return new rgb_t(reader.ReadUInt32());
            if (type == typeof(attotime)) return new attotime(reader.ReadInt32(), reader.ReadInt64());
            if (type == typeof(long)) return reader.ReadInt64();
            if (type == typeof(ulong)) return reader.ReadUInt64();
            if (type == typeof(float)) return reader.ReadSingle();
            if (type == typeof(double)) return reader.ReadDouble();
            throw new InvalidDataException($"Unsupported MCS savestate primitive type: {type.FullName}");
        }

        static void SetArrayLinearValue(Array array, int linearIndex, object value)
        {
            if (array.Rank == 1)
            {
                array.SetValue(value, linearIndex);
                return;
            }

            int[] indices = new int[array.Rank];
            for (int axis = array.Rank - 1; axis >= 0; axis--)
            {
                int length = array.GetLength(axis);
                indices[axis] = linearIndex % length;
                linearIndex /= length;
            }

            array.SetValue(value, indices);
        }
    }
}
