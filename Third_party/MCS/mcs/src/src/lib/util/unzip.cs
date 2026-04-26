// license:BSD-3-Clause
// copyright-holders:Edward Fast

using System;
using System.Collections.Generic;
using System.IO;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;

using int64_t = System.Int64;
using PointerU8 = mame.Pointer<System.Byte>;
using uint8_t = System.Byte;
using uint32_t = System.UInt32;
using uint64_t = System.UInt64;

using static mame.corestr_global;
using static mame.cpp_global;


namespace mame
{
    public static partial class util
    {
        // describes an open archive file
        public abstract class archive_file : IDisposable
        {
            // Error types
            public enum error : int
            {
                BAD_SIGNATURE = 1,
                DECOMPRESS_ERROR,
                FILE_TRUNCATED,
                FILE_CORRUPT,
                UNSUPPORTED,
                BUFFER_TOO_SMALL
            }

            //typedef std::unique_ptr<archive_file> ptr;


            /* ----- archive file access ----- */

            // open a ZIP file and parse its central directory
            /*-------------------------------------------------
                zip_file_open - opens a ZIP file for reading
            -------------------------------------------------*/
            public static std.error_condition open_zip(string filename, out archive_file result)
            {
                result = null;

                try
                {
                    if (!File.Exists(filename))
                        return std.errc.no_such_file_or_directory;

                    ZipArchive archive = ZipArchive.Open(filename);
                    result = new sharp_zip_archive_file(archive);
                    return new std.error_condition();
                }
                catch (UnauthorizedAccessException)
                {
                    return std.errc.permission_denied;
                }
                catch (FileNotFoundException)
                {
                    return std.errc.no_such_file_or_directory;
                }
                catch (DirectoryNotFoundException)
                {
                    return std.errc.no_such_file_or_directory;
                }
                catch (IOException)
                {
                    return std.errc.io_error;
                }
                catch
                {
                    return std.errc.invalid_argument;
                }
            }

            //static std::error_condition open_zip(std::unique_ptr<random_read> &&file, ptr &result) noexcept;


            // open a 7Z file and parse its central directory
            public static std.error_condition open_7z(string filename, out archive_file result)
            {
                // ensure we start with a nullptr result
                result = null;  //result.reset();

                // see if we are in the cache, and reopen if so
                m7z_file_impl newimpl = m7z_file_impl.find_cached(filename);  //m7z_file_impl::ptr newimpl(m7z_file_impl::find_cached(filename));

                if (newimpl == null)
                {
                    // allocate memory for the 7z file structure
                    try { newimpl = new m7z_file_impl(filename); }  //try { newimpl = std.make_unique<m7z_file_impl>(filename); }
                    catch (Exception e) { return std.errc.not_enough_memory; }
                    var err = newimpl.initialize();
                    if (err)
                        return err;
                }

                // allocate the archive API wrapper
                result = new m7z_file_wrapper(newimpl);  //result.reset(new (std::nothrow) m7z_file_wrapper(std::move(newimpl)));
                if (result != null)
                {
                    return new std.error_condition();
                }
                else
                {
                    m7z_file_impl.close(newimpl);  //m7z_file_impl::close(std::move(newimpl));
                    return std.errc.not_enough_memory;
                }
            }

            //static std::error_condition open_7z(std::unique_ptr<random_read> &&file, ptr &result) noexcept;


            // close an archive file (may actually be left open due to caching)
            ~archive_file()
            {
                assert(m_isDisposed);  // can remove
            }

            bool m_isDisposed = false;
            public virtual void Dispose()
            {
                m_isDisposed = true;
            }


            // clear out all open files from the cache
            public static void cache_clear()
            {
                zip_file_impl.cache_clear();
                m7z_file_impl.m7z_file_cache_clear();
            }


            /* ----- contained file access ----- */

            // iterating over files - returns negative on reaching end
            protected abstract int first_file();
            protected abstract int next_file();


            // find a file index by crc, filename or both - returns non-negative on match
            public abstract int search(uint32_t crc);
            public abstract int search(string filename, bool partialpath);
            public abstract int search(uint32_t crc, string filename, bool partialpath);


            // information on most recently found file
            protected abstract bool current_is_directory();
            protected abstract string current_name();
            public abstract uint64_t current_uncompressed_length();
            protected abstract int64_t current_last_modified();  //virtual std::chrono::system_clock::time_point current_last_modified() const = 0;
            public abstract uint32_t current_crc();


            // decompress the most recently found file in the ZIP
            public abstract std.error_condition decompress(PointerU8 buffer, uint32_t length);  //void *buffer, std::uint32_t length)
        }


        // error category for archive errors
        //std::error_category const &archive_category() noexcept;
        //inline std::error_condition make_error_condition(archive_file::error err) noexcept { return std::error_condition(int(err), archive_category()); }


        //class archive_category_impl : public std::error_category


        class sharp_zip_archive_file : archive_file
        {
            readonly ZipArchive m_archive;
            readonly List<IArchiveEntry> m_entries;
            int m_curr_file_idx;
            string m_curr_name;
            bool m_curr_is_dir;
            uint64_t m_curr_length;
            uint32_t m_curr_crc;


            public sharp_zip_archive_file(ZipArchive archive)
            {
                m_archive = archive;
                m_entries = new List<IArchiveEntry>();
                foreach (IArchiveEntry entry in archive.Entries)
                    m_entries.Add(entry);

                m_curr_file_idx = -1;
                m_curr_name = "";
                m_curr_is_dir = false;
                m_curr_length = 0;
                m_curr_crc = 0;
            }


            public override void Dispose()
            {
                m_archive.Dispose();
                base.Dispose();
            }


            protected override int first_file()
            {
                return search(0, 0, "", false, false, false);
            }


            protected override int next_file()
            {
                return m_curr_file_idx < 0 ? -1 : search(m_curr_file_idx + 1, 0, "", false, false, false);
            }


            public override int search(uint32_t crc)
            {
                return search(0, crc, "", true, false, false);
            }


            public override int search(string filename, bool partialpath)
            {
                return search(0, 0, filename, false, true, partialpath);
            }


            public override int search(uint32_t crc, string filename, bool partialpath)
            {
                return search(0, crc, filename, true, true, partialpath);
            }


            protected override bool current_is_directory() { return m_curr_is_dir; }
            protected override string current_name() { return m_curr_name; }
            public override uint64_t current_uncompressed_length() { return m_curr_length; }
            protected override int64_t current_last_modified() { throw new emu_unimplemented(); }
            public override uint32_t current_crc() { return m_curr_crc; }


            public override std.error_condition decompress(PointerU8 buffer, uint32_t length)
            {
                if (m_curr_file_idx < 0 || m_curr_file_idx >= m_entries.Count)
                    return std.errc.bad_file_descriptor;

                IArchiveEntry entry = m_entries[m_curr_file_idx];
                if (entry.IsDirectory || entry.IsEncrypted)
                    return std.errc.not_supported;

                if ((uint64_t)length < m_curr_length)
                    return std.errc.no_buffer_space;

                try
                {
                    using (Stream stream = entry.OpenEntryStream())
                    {
                        if (stream == null)
                            return std.errc.io_error;

                        byte [] chunk = new byte [8192];
                        uint32_t offset = 0;
                        while (true)
                        {
                            int read = stream.Read(chunk, 0, chunk.Length);
                            if (read <= 0)
                                break;

                            if ((uint64_t)offset + (uint64_t)read > length)
                                return std.errc.no_buffer_space;

                            for (int i = 0; i < read; i++)
                                buffer[offset + (uint32_t)i] = chunk[i];

                            offset += (uint32_t)read;
                        }

                        return new std.error_condition();
                    }
                }
                catch (IOException)
                {
                    return std.errc.io_error;
                }
                catch
                {
                    return std.errc.invalid_argument;
                }
            }


            int search(
                int start,
                uint32_t search_crc,
                string search_filename,
                bool matchcrc,
                bool matchname,
                bool partialpath)
            {
                string wanted = normalize_path(search_filename);

                for (int i = Math.Max(0, start); i < m_entries.Count; i++)
                {
                    IArchiveEntry entry = m_entries[i];
                    bool is_dir = entry.IsDirectory;
                    string name = normalize_path(entry.Key);
                    uint32_t crc = unchecked((uint32_t)entry.Crc);
                    bool crcmatch = !is_dir && crc == search_crc;
                    bool found;

                    if (!matchname)
                    {
                        found = !matchcrc || crcmatch;
                    }
                    else
                    {
                        bool namematch = wanted.Length == name.Length && (wanted.Length == 0 || core_stricmp(wanted, name) == 0);
                        int partialoffset = name.Length - wanted.Length;
                        bool partialmatch = partialpath
                                            && name.Length > wanted.Length
                                            && partialoffset > 0
                                            && name[partialoffset - 1] == '/'
                                            && (wanted.Length == 0 || core_strnicmp(wanted, name.Substring(partialoffset), (uint64_t)wanted.Length) == 0);
                        found = (!matchcrc || crcmatch) && (namematch || partialmatch);
                    }

                    if (!found)
                        continue;

                    m_curr_name = name;
                    m_curr_file_idx = i;
                    m_curr_is_dir = is_dir;
                    m_curr_length = (uint64_t)Math.Max(0L, entry.Size);
                    m_curr_crc = crc;
                    return i;
                }

                return -1;
            }


            static string normalize_path(string path)
            {
                return (path ?? "").Replace('\\', '/');
            }
        }


        class zip_file_impl
        {
            //using ptr = std::unique_ptr<zip_file_impl>;

            //zip_file_impl(std::string &&filename) noexcept
            //    : m_filename(std::move(filename))
            //{
            //    std::fill(m_buffer.begin(), m_buffer.end(), 0);
            //}

            //zip_file_impl(random_read::ptr &&file) noexcept
            //    : zip_file_impl(std::string())
            //{
            //    m_file = std::move(file);
            //}

            //static ptr find_cached(std::string_view filename) noexcept

            //static void close(ptr &&zip) noexcept;


            public static void cache_clear()
            {
                //throw new emu_unimplemented();
#if false
                // clear call cache entries
                std::lock_guard<std::mutex> guard(s_cache_mutex);
                for (std::size_t cachenum = 0; cachenum < s_cache.size(); s_cache[cachenum++].reset()) { }
#endif
            }

            //std::error_condition initialize() noexcept

            //int first_file() noexcept

            //int next_file() noexcept

            //int search(std::uint32_t crc) noexcept

            //int search(std::string_view filename, bool partialpath) noexcept

            //int search(std::uint32_t crc, std::string_view filename, bool partialpath) noexcept

            //bool current_is_directory() const noexcept { return m_curr_is_dir; }

            //const std::string &current_name() const noexcept { return m_header.file_name; }

            //std::uint64_t current_uncompressed_length() const noexcept { return m_header.uncompressed_length; }

            //std::chrono::system_clock::time_point current_last_modified() const noexcept

            //std::uint32_t current_crc() const noexcept { return m_header.crc; }

            //std::error_condition decompress(void *buffer, std::size_t length) noexcept;

            //int search(std::uint32_t search_crc, std::string_view search_filename, bool matchcrc, bool matchname, bool partialpath) noexcept;

            //std::error_condition reopen() noexcept

            //static std::chrono::system_clock::time_point decode_dos_time(std::uint16_t date, std::uint16_t time) noexcept

            // ZIP file parsing
            //std::error_condition read_ecd() noexcept;
            //std::error_condition get_compressed_data_offset(std::uint64_t &offset) noexcept;

            // decompression interfaces
            //std::error_condition decompress_data_type_0(std::uint64_t offset, void *buffer, std::size_t length) noexcept;
            //std::error_condition decompress_data_type_8(std::uint64_t offset, void *buffer, std::size_t length) noexcept;
            //std::error_condition decompress_data_type_14(std::uint64_t offset, void *buffer, std::size_t length) noexcept;

            //struct file_header

            // contains extracted end of central directory information
            //struct ecd
        }


        //class zip_file_wrapper : public archive_file

        //class reader_base

        //class extra_field_reader : private reader_base

        //class local_file_header_reader : private reader_base

        //class central_dir_entry_reader : private reader_base

        //class ecd64_reader : private reader_base

        //class ecd64_locator_reader : private reader_base

        //class ecd_reader : private reader_base

        //class zip64_ext_info_reader : private reader_base

        //class utf8_path_reader : private reader_base

        //class ntfs_tag_reader : private reader_base

        //class ntfs_reader : private reader_base

        //class ntfs_times_reader : private reader_base

        //class general_flag_reader
    }


    //namespace std {
    //template <> struct is_error_condition_enum<util::archive_file::error> : public std::true_type { };
    //} // namespace std
}
