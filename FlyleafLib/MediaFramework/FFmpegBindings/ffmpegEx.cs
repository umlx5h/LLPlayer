﻿namespace Flyleaf.FFmpeg;

/// <summary>
/// Additional bindings required by Flyleaf (mostly private- ensure dll's are same versions)
/// </summary>
internal unsafe partial class ffmpegEx
{
#pragma warning disable CS0649
#pragma warning disable CS0169
    #region hls demuxer
    public unsafe struct HLSContext
    {
        public AVClass *avclass;
        public AVFormatContext *ctx;
        public int n_variants;
        public void **variants; // variant

        public int n_playlists;
        public void **playlists; // HLSPlaylist
        public int n_renditions;
        public void **renditions;

        public long cur_seq_no;
        public int m3u8_hold_counters;
        public int live_start_index;
        public int first_packet;
        public long first_timestamp;
        public long cur_timestamp;
    }

    public struct Segment
    {
        public long duration;
        public long url_offset;
        public long size;
        public byte *url;
        public byte *key;
        public KeyType key_type;
        public byte_array16 iv;
        /* associated Media Initialization Section, treated as a segment */
        public Segment *init_section;
    }

    public enum KeyType : int
    {
        KEY_NONE = 0,
        KEY_AES_128 = 1,
        KEY_SAMPLE_AES = 2
    }
    public enum PlaylistType : int
    {
        PLS_TYPE_UNSPECIFIED = 0,
        PLS_TYPE_EVENT = 1,
        PLS_TYPE_VOD = 2
    }
    public unsafe struct HLSPlaylist // latest
    {
        public byte_array4096 url;
        public FFIOContext pb;
        public byte* read_buffer;
        public AVIOContext *input;
        public int input_read_done;
        public AVIOContext *input_next;
        public int input_next_requested;
        public AVFormatContext *parent;
        public int index;
        public AVFormatContext *ctx;
        public AVPacket *pkt;
        public int has_noheader_flag;

        /* main demuxer streams associated with this playlist
         * indexed by the subdemuxer stream indexes */
        public AVStream **main_streams;
        public int n_main_streams;

        public int finished;
        public PlaylistType type;
        public long target_duration;
        public long start_seq_no;
        public int time_offset_flag;
        public long start_time_offset;
        public int n_segments;
        public Segment **segments;
        public int needed;
        public int broken;
        public long cur_seq_no;
        public long last_seq_no;
        public int m3u8_hold_counters;
        public long cur_seg_offset;
        public long last_load_time;

        /* Currently active Media Initialization Section */
        public void *cur_init_section;
        public byte *init_sec_buf;
        public uint init_sec_buf_size;
        public uint init_sec_data_len;
        public uint init_sec_buf_read_offset;

        public byte_array4096 key_url;
        public byte_array16 key;

        /* ID3 timestamp handling (elementary audio streams have ID3 timestamps
         * (and possibly other ID3 tags) in the beginning of each segment) */
        public int is_id3_timestamped; /* -1: not yet known */
        public long id3_mpegts_timestamp; /* in mpegts tb */
        public long id3_offset; /* in stream original tb */
        public byte* id3_buf; /* temp buffer for id3 parsing */
        public uint id3_buf_size;
        public AVDictionary *id3_initial; /* data from first id3 tag */
        public int id3_found; /* ID3 tag found at some point */
        public int id3_changed; /* ID3 tag data has changed at some point */
        public void *id3_deferred_extra; /* stored here until subdemuxer is opened */
    }
    #endregion

    #region misc
    public struct FFIOContext
    {
        AVIOContext pub;
        public long t0;
        public long t1;
        public long t2;
        public long t3;
        public long t4;
        public long t5;
        public long t6;
        public int  tt1;
        public int  tt2;
        public int  tt3;
    }

    public unsafe struct byte_array4096
    {
        public static readonly int Size = 4096;
        fixed byte _[4096];

        public byte this[uint i]
        {
            get { if (i >= Size) throw new ArgumentOutOfRangeException(); fixed (byte_array4096* p = &this) { return p->_[i]; } }
            set { if (i >= Size) throw new ArgumentOutOfRangeException(); fixed (byte_array4096* p = &this) { p->_[i] = value; } }
        }
        public byte[] ToArray()
        {
            fixed (byte_array4096* p = &this) { byte[] a = new byte[Size]; for (uint i = 0; i < Size; i++) a[i] = p->_[i]; return a; }
        }
        public void UpdateFrom(byte[] array)
        {
            fixed (byte_array4096* p = &this) { uint i = 0; foreach(byte value in array) { p->_[i++] = value; if (i >= Size) return; } }
        }
        public static implicit operator byte[](byte_array4096 @struct) => @struct.ToArray();
    }

    public unsafe struct byte_array16
    {
        public static readonly int Size = 16;
        fixed byte _[16];

        public byte this[uint i]
        {
            get { if (i >= Size) throw new ArgumentOutOfRangeException(); fixed (byte_array16* p = &this) { return p->_[i]; } }
            set { if (i >= Size) throw new ArgumentOutOfRangeException(); fixed (byte_array16* p = &this) { p->_[i] = value; } }
        }
        public byte[] ToArray()
        {
            fixed (byte_array16* p = &this) { byte[] a = new byte[Size]; for (uint i = 0; i < Size; i++) a[i] = p->_[i]; return a; }
        }
        public void UpdateFrom(byte[] array)
        {
            fixed (byte_array16* p = &this) { uint i = 0; foreach(byte value in array) { p->_[i++] = value; if (i >= Size) return; } }
        }
        public static implicit operator byte[](byte_array16 @struct) => @struct.ToArray();
    }
    #endregion
#pragma warning restore CS0169
#pragma warning restore CS0649
}
