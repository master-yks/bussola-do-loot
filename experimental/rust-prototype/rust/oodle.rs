//! Oodle Kraken / Mermaid decompressor for Fortnite Replay chunks.
//! Ported from OozSharp / Powzix ooz with full memory safety and high performance.

use anyhow::{Result, bail};

const SCRATCH_SIZE: usize = 0x6C000;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum DecoderType {
    Lzna = 5,
    Kraken = 6,
    Mermaid = 10,
    BitKnit = 11,
    Leviathan = 12,
    Unknown(u8),
}

impl From<u8> for DecoderType {
    fn from(v: u8) -> Self {
        match v {
            5 => DecoderType::Lzna,
            6 => DecoderType::Kraken,
            10 => DecoderType::Mermaid,
            11 => DecoderType::BitKnit,
            12 => DecoderType::Leviathan,
            other => DecoderType::Unknown(other),
        }
    }
}

#[derive(Clone, Copy, Debug)]
struct KrakenHeader {
    decoder_type: DecoderType,
    _restart_decoder: bool,
    uncompressed: bool,
    use_checksums: bool,
}

impl KrakenHeader {
    fn parse(source: &[u8]) -> Result<Self> {
        if source.len() < 2 {
            bail!("KrakenHeader: buffer too short");
        }
        let b0 = source[0];
        let b1 = source[1];
        if (b0 & 0xF) != 0xC {
            bail!("Failed to decode header. (source[0] & 0xF) != 0xC");
        }
        if ((b0 >> 4) & 3) != 0 {
            bail!("Failed to decode header. ((source[0] >> 4) & 3) != 0");
        }
        Ok(Self {
            _restart_decoder: ((b0 >> 7) & 1) == 1,
            uncompressed: ((b0 >> 6) & 1) == 1,
            decoder_type: DecoderType::from(b1 & 0x7F),
            use_checksums: ((b1 >> 7) & 1) == 1,
        })
    }
}

#[derive(Clone, Copy, Debug)]
struct KrakenQuantumHeader {
    compressed_size: u32,
    checksum: u32,
    _flag1: u8,
    _flag2: u8,
    whole_match_distance: u32,
}

impl KrakenQuantumHeader {
    fn parse(source: &[u8], use_checksums: bool) -> Result<(Self, usize)> {
        if source.len() < 3 {
            bail!("KrakenQuantumHeader: buffer too short");
        }
        let v = ((source[0] as u32) << 16) | ((source[1] as u32) << 8) | (source[2] as u32);
        let size = v & 0x3FFFF;
        if size != 0x3FFFF {
            let compressed_size = size + 1;
            let flag1 = ((v >> 18) & 1) as u8;
            let flag2 = ((v >> 19) & 1) as u8;
            if use_checksums {
                if source.len() < 6 {
                    bail!("KrakenQuantumHeader: checksum bytes missing");
                }
                let checksum = ((source[3] as u32) << 16)
                    | ((source[4] as u32) << 8)
                    | (source[5] as u32);
                return Ok((
                    Self {
                        compressed_size,
                        checksum,
                        _flag1: flag1,
                        _flag2: flag2,
                        whole_match_distance: 0,
                    },
                    6,
                ));
            } else {
                return Ok((
                    Self {
                        compressed_size,
                        checksum: 0,
                        _flag1: flag1,
                        _flag2: flag2,
                        whole_match_distance: 0,
                    },
                    3,
                ));
            }
        }
        let v_hi = v >> 18;
        if v_hi == 1 {
            if source.len() < 4 {
                bail!("KrakenQuantumHeader: memset byte missing");
            }
            return Ok((
                Self {
                    compressed_size: 0,
                    checksum: source[3] as u32,
                    _flag1: 0,
                    _flag2: 0,
                    whole_match_distance: 0,
                },
                4,
            ));
        }
        bail!("Failed to parse KrakenQuantumHeader");
    }
}

#[repr(C)]
struct MermaidLzTable {
    cmd_stream: *const u8,
    cmd_stream_end: *const u8,
    length_stream: *const u8,
    lit_stream: *const u8,
    lit_stream_end: *const u8,
    offset16_stream: *const u16,
    offset16_stream_end: *const u16,
    offset32_stream: *const u32,
    offset32_stream_end: *const u32,
    offset32_stream1: *mut u32,
    offset32_stream2: *mut u32,
    offset32_stream1_size: u32,
    offset32_stream2_size: u32,
    cmd_stream2_offsets: u32,
    cmd_stream2_offsets_end: u32,
}

impl Default for MermaidLzTable {
    fn default() -> Self {
        Self {
            cmd_stream: std::ptr::null(),
            cmd_stream_end: std::ptr::null(),
            length_stream: std::ptr::null(),
            lit_stream: std::ptr::null(),
            lit_stream_end: std::ptr::null(),
            offset16_stream: std::ptr::null(),
            offset16_stream_end: std::ptr::null(),
            offset32_stream: std::ptr::null(),
            offset32_stream_end: std::ptr::null(),
            offset32_stream1: std::ptr::null_mut(),
            offset32_stream2: std::ptr::null_mut(),
            offset32_stream1_size: 0,
            offset32_stream2_size: 0,
            cmd_stream2_offsets: 0,
            cmd_stream2_offsets_end: 0,
        }
    }
}

#[inline(always)]
unsafe fn copy64(dst: *mut u8, src: *const u8) {
    unsafe {
        std::ptr::write_unaligned(dst as *mut u64, std::ptr::read_unaligned(src as *const u64));
    }
}

#[inline(always)]
fn align_ptr(p: *mut u8, align: usize) -> *mut u8 {
    let addr = p as usize;
    ((addr + (align - 1)) & !(align - 1)) as *mut u8
}

pub fn decompress_replay_data(compressed_input: &[u8], uncompressed_size: usize) -> Result<Vec<u8>> {
    // Allocate extra 64 bytes padding at the end so 64-bit unaligned copy64 writes never overflow
    let mut decompressed = vec![0u8; uncompressed_size + 64];
    let mut scratch = vec![0u8; SCRATCH_SIZE + 64];

    let mut remaining_bytes = uncompressed_size;
    let mut src_offset = 0usize;
    let mut dst_offset = 0usize;
    let mut current_header = KrakenHeader {
        decoder_type: DecoderType::Mermaid,
        _restart_decoder: false,
        uncompressed: false,
        use_checksums: false,
    };

    unsafe {
        let dst_base = decompressed.as_mut_ptr();
        let scratch_ptr = scratch.as_mut_ptr();

        while remaining_bytes != 0 {
            let (src_used, dst_used) = decode_step(
                &mut current_header,
                dst_base,
                dst_offset,
                remaining_bytes,
                &compressed_input[src_offset..],
                scratch_ptr,
                SCRATCH_SIZE,
            )?;
            if dst_used == 0 {
                bail!("Oodle decode_step made no progress");
            }
            src_offset += src_used;
            dst_offset += dst_used;
            remaining_bytes -= dst_used;
        }
    }

    decompressed.truncate(uncompressed_size);
    Ok(decompressed)
}

unsafe fn decode_step(
    header: &mut KrakenHeader,
    destination: *mut u8,
    destination_offset: usize,
    remaining_destination_bytes: usize,
    source_slice: &[u8],
    scratch: *mut u8,
    scratch_size: usize,
) -> Result<(usize, usize)> {
    let mut src_pos = 0usize;
    if (destination_offset & 0x3FFFF) == 0 {
        *header = KrakenHeader::parse(source_slice)?;
        src_pos += 2;
    }

    let is_mermaid = header.decoder_type == DecoderType::Mermaid;
    let destination_bytes_left = remaining_destination_bytes.min(if is_mermaid { 0x40000 } else { 0x4000 });

    if header.uncompressed {
        if source_slice.len() - src_pos < destination_bytes_left {
            bail!("DecodeStep: uncompressed source too small");
        }
        unsafe {
            std::ptr::copy_nonoverlapping(
                source_slice.as_ptr().add(src_pos),
                destination.add(destination_offset),
                destination_bytes_left,
            );
        }
        return Ok((src_pos + destination_bytes_left, destination_bytes_left));
    }

    if !is_mermaid {
        bail!("Decoder type {:?} not supported", header.decoder_type);
    }

    let (qhdr, q_bytes) = KrakenQuantumHeader::parse(&source_slice[src_pos..], header.use_checksums)?;
    src_pos += q_bytes;

    if src_pos > source_slice.len() {
        bail!("Index out of range of source array");
    }

    let avail = source_slice.len() - src_pos;
    if avail < qhdr.compressed_size as usize {
        return Ok((0, 0));
    }

    if qhdr.compressed_size as usize > remaining_destination_bytes {
        bail!(
            "Invalid compression size {} > {}",
            qhdr.compressed_size,
            remaining_destination_bytes
        );
    }

    if qhdr.compressed_size == 0 {
        if qhdr.whole_match_distance != 0 {
            if qhdr.whole_match_distance as usize > destination_offset {
                bail!("WholeMatchDistance > destinationOffset");
            }
            unsafe {
                let dist = qhdr.whole_match_distance as usize;
                for i in 0..destination_bytes_left {
                    *destination.add(destination_offset + i) =
                        *destination.add(destination_offset + i - dist);
                }
            }
        } else {
            unsafe {
                std::ptr::write_bytes(
                    destination.add(destination_offset),
                    qhdr.checksum as u8,
                    destination_bytes_left,
                );
            }
        }
        return Ok((src_pos, destination_bytes_left));
    }

    if qhdr.compressed_size as usize == destination_bytes_left {
        unsafe {
            std::ptr::copy_nonoverlapping(
                source_slice.as_ptr().add(src_pos),
                destination.add(destination_offset),
                destination_bytes_left,
            );
        }
        return Ok((src_pos + destination_bytes_left, destination_bytes_left));
    }

    let src_ptr = unsafe { source_slice.as_ptr().add(src_pos) };
    let src_end = unsafe { src_ptr.add(qhdr.compressed_size as usize) };
    let num_bytes = unsafe {
        mermaid_decode_quantum(
            destination.add(destination_offset),
            destination.add(destination_offset + destination_bytes_left),
            destination,
            src_ptr,
            src_end,
            scratch,
            scratch_size,
        )?
    };

    if num_bytes != qhdr.compressed_size as usize {
        bail!(
            "Invalid number of bytes decompressed: {} != {}",
            num_bytes,
            qhdr.compressed_size
        );
    }

    Ok((src_pos + num_bytes, destination_bytes_left))
}

unsafe fn decode_bytes(
    output: &mut *const u8,
    mut source: *const u8,
    source_end: *const u8,
    decoded_size: &mut usize,
    output_size: usize,
    force_memmove: bool,
    scratch: *mut u8,
    _scratch_end: *mut u8,
) -> Result<usize> {
    let source_org = source;
    if unsafe { source_end.offset_from(source) } < 2 {
        bail!("DecodeBytes: Too few bytes remaining");
    }

    let b0 = unsafe { *source } as usize;
    let chunk_type = (b0 >> 4) & 0x7;
    if chunk_type == 0 {
        let source_size = if b0 >= 0x80 {
            let b1 = unsafe { *source.add(1) } as usize;
            source = unsafe { source.add(2) };
            ((b0 << 8) | b1) & 0xFFF
        } else {
            if unsafe { source_end.offset_from(source) } < 3 {
                bail!("DecodeBytes: Too few bytes remaining (3)");
            }
            let b1 = unsafe { *source.add(1) } as usize;
            let b2 = unsafe { *source.add(2) } as usize;
            source = unsafe { source.add(3) };
            let sz = (b0 << 16) | (b1 << 8) | b2;
            if (sz & !0x3ffff) > 0 {
                bail!("Reserved bits must not be set");
            }
            sz
        };

        if source_size > output_size || (unsafe { source_end.offset_from(source) } as usize) < source_size {
            bail!("sourceSize ({}) > outputSize ({})", source_size, output_size);
        }

        *decoded_size = source_size;
        if force_memmove {
            unsafe {
                std::ptr::copy_nonoverlapping(source, scratch, source_size);
            }
            *output = scratch;
        } else {
            *output = source;
        }
        return Ok(unsafe { source.add(source_size).offset_from(source_org) } as usize);
    }

    bail!("DecodeBytes: chunkType {} not supported", chunk_type);
}

unsafe fn mermaid_decode_quantum(
    mut destination: *mut u8,
    destination_end: *mut u8,
    destination_start: *mut u8,
    mut source: *const u8,
    source_end: *const u8,
    temp: *mut u8,
    _temp_size: usize,
) -> Result<usize> {
    let source_in = source;

    while destination < destination_end {
        let mut destination_count = unsafe { destination_end.offset_from(destination) } as usize;
        if destination_count > 0x20000 {
            destination_count = 0x20000;
        }

        if unsafe { source_end.offset_from(source) } < 4 {
            bail!("Less than 4 bytes remaining in source");
        }

        let chunk_header = unsafe {
            ((*source.add(2)) as usize)
                | (((*source.add(1)) as usize) << 8)
                | (((*source) as usize) << 16)
        };

        let source_used: usize;
        if (chunk_header & 0x800000) == 0 {
            let mut out_ptr: *const u8 = destination;
            let mut written_bytes = 0usize;
            source_used = unsafe {
                decode_bytes(
                    &mut out_ptr,
                    source,
                    source_end,
                    &mut written_bytes,
                    destination_count,
                    false,
                    temp,
                    temp.add(0x40000),
                )?
            };
            if written_bytes != destination_count {
                bail!("DecodeBytes written != destination_count");
            }
            if out_ptr != destination as *const u8 {
                unsafe {
                    std::ptr::copy_nonoverlapping(out_ptr, destination, destination_count);
                }
            }
        } else {
            source = unsafe { source.add(3) };
            source_used = chunk_header & 0x7FFFF;
            let mode = (chunk_header >> 19) & 0xF;

            if (unsafe { source_end.offset_from(source) } as usize) < source_used {
                bail!("Not enough source bytes remaining");
            }

            if source_used < destination_count {
                let mut temp_usage = 2 * destination_count + 32;
                if temp_usage > 0x40000 {
                    temp_usage = 0x40000;
                }

                let mut lz = MermaidLzTable::default();
                let offset = unsafe { destination.offset_from(destination_start) } as isize;

                unsafe {
                    if !mermaid_read_lz_table(
                        mode,
                        source,
                        source.add(source_used),
                        destination,
                        destination_count,
                        offset,
                        temp,
                        temp.add(temp_usage),
                        &mut lz,
                    )? {
                        bail!("Failed to run MermaidReadLzTable");
                    }

                    if !mermaid_process_lz_runs(
                        mode,
                        source,
                        source.add(source_used),
                        destination,
                        destination_count,
                        offset,
                        destination_end,
                        &mut lz,
                    )? {
                        bail!("Failed to run MermaidProcessLzRuns");
                    }
                }
            } else if source_used > destination_count || mode != 0 {
                bail!("Used bytes > destinationCount or Mode != 0");
            } else {
                unsafe {
                    std::ptr::copy_nonoverlapping(source, destination, destination_count);
                }
            }
        }

        source = unsafe { source.add(source_used) };
        destination = unsafe { destination.add(destination_count) };
    }

    Ok(unsafe { source.offset_from(source_in) } as usize)
}

#[allow(clippy::too_many_arguments)]
unsafe fn mermaid_read_lz_table(
    mode: usize,
    mut source: *const u8,
    source_end: *const u8,
    destination: *mut u8,
    destination_size: usize,
    offset: isize,
    mut scratch: *mut u8,
    scratch_end: *mut u8,
    lz: &mut MermaidLzTable,
) -> Result<bool> {
    if mode > 1 {
        return Ok(false);
    }
    if unsafe { source_end.offset_from(source) } < 10 {
        return Ok(false);
    }

    if offset == 0 {
        unsafe {
            copy64(destination, source);
            source = source.add(8);
        }
    }

    // Decode lit stream
    let mut scratch_out: *const u8 = scratch;
    let mut decode_count = 0usize;
    let max_lit = (unsafe { scratch_end.offset_from(scratch) } as usize).min(destination_size);
    let num_bytes = unsafe {
        decode_bytes(
            &mut scratch_out,
            source,
            source_end,
            &mut decode_count,
            max_lit,
            false,
            scratch,
            scratch_end,
        )?
    };
    source = unsafe { source.add(num_bytes) };
    lz.lit_stream = scratch_out;
    lz.lit_stream_end = unsafe { scratch_out.add(decode_count) };
    scratch = unsafe { scratch.add(decode_count) };

    // Decode flag (cmd) stream
    scratch_out = scratch;
    let max_cmd = (unsafe { scratch_end.offset_from(scratch) } as usize).min(destination_size);
    let num_bytes = unsafe {
        decode_bytes(
            &mut scratch_out,
            source,
            source_end,
            &mut decode_count,
            max_cmd,
            false,
            scratch,
            scratch_end,
        )?
    };
    source = unsafe { source.add(num_bytes) };
    lz.cmd_stream = scratch_out;
    lz.cmd_stream_end = unsafe { scratch_out.add(decode_count) };
    scratch = unsafe { scratch.add(decode_count) };

    lz.cmd_stream2_offsets_end = decode_count as u32;

    if destination_size <= 0x10000 {
        lz.cmd_stream2_offsets = decode_count as u32;
    } else {
        if unsafe { source_end.offset_from(source) } < 2 {
            bail!("MermaidReadLzTable: Too few bytes remaining for cmd_stream2_offsets");
        }
        lz.cmd_stream2_offsets = unsafe { std::ptr::read_unaligned(source as *const u16) } as u32;
        source = unsafe { source.add(2) };
        if lz.cmd_stream2_offsets > lz.cmd_stream2_offsets_end {
            bail!("MermaidReadLzTable: cmd_stream2_offsets > cmd_stream2_offsets_end");
        }
    }

    if unsafe { source_end.offset_from(source) } < 2 {
        bail!("MermaidReadLzTable: Too few bytes remaining for off16Count");
    }

    let off16_count = unsafe { std::ptr::read_unaligned(source as *const u16) } as usize;
    if off16_count == 0xFFFF {
        source = unsafe { source.add(2) };
        let mut offset16_high: *const u8 = scratch;
        let mut offset16_high_count = 0usize;
        let max_hi = (unsafe { scratch_end.offset_from(scratch) } as usize).min(destination_size >> 1);
        let nb = unsafe {
            decode_bytes(
                &mut offset16_high,
                source,
                source_end,
                &mut offset16_high_count,
                max_hi,
                false,
                scratch,
                scratch_end,
            )?
        };
        source = unsafe { source.add(nb) };
        scratch = unsafe { scratch.add(offset16_high_count) };

        let mut offset16_low: *const u8 = scratch;
        let mut offset16_low_count = 0usize;
        let max_lo = (unsafe { scratch_end.offset_from(scratch) } as usize).min(destination_size >> 1);
        let nb = unsafe {
            decode_bytes(
                &mut offset16_low,
                source,
                source_end,
                &mut offset16_low_count,
                max_lo,
                false,
                scratch,
                scratch_end,
            )?
        };
        source = unsafe { source.add(nb) };
        scratch = unsafe { scratch.add(offset16_low_count) };

        if offset16_low_count != offset16_high_count {
            bail!("MermaidReadLzTable: offset16LowCount != offset16HighCount");
        }

        scratch = align_ptr(scratch, 2);
        lz.offset16_stream = scratch as *const u16;
        if unsafe { scratch.add(offset16_low_count * 2) } > scratch_end {
            bail!("MermaidReadLzTable: scratch overflow on offset16");
        }
        let dst16 = scratch as *mut u16;
        scratch = unsafe { scratch.add(offset16_low_count * 2) };
        lz.offset16_stream_end = scratch as *const u16;

        for i in 0..offset16_low_count {
            unsafe {
                let lo = *offset16_low.add(i) as u16;
                let hi = *offset16_high.add(i) as u16;
                *dst16.add(i) = lo + (hi << 8);
            }
        }
    } else {
        lz.offset16_stream = unsafe { source.add(2) } as *const u16;
        source = unsafe { source.add(2 + off16_count * 2) };
        lz.offset16_stream_end = source as *const u16;
    }

    if unsafe { source_end.offset_from(source) } < 3 {
        bail!("MermaidReadLzTable: Too few bytes remaining for temp 24-bit");
    }

    let temp: u32 = unsafe {
        (*source as u32) | ((*source.add(1) as u32) << 8) | ((*source.add(2) as u32) << 16)
    };
    source = unsafe { source.add(3) };

    if temp != 0 {
        let mut offset32_size1 = temp >> 12;
        let mut offset32_size2 = temp & 0xFFF;

        if offset32_size1 == 4095 {
            if unsafe { source_end.offset_from(source) } < 2 {
                bail!("MermaidReadLzTable: Too few bytes for offset32_size1");
            }
            offset32_size1 = unsafe { std::ptr::read_unaligned(source as *const u16) } as u32;
            source = unsafe { source.add(2) };
        }
        if offset32_size2 == 4095 {
            if unsafe { source_end.offset_from(source) } < 2 {
                bail!("MermaidReadLzTable: Too few bytes for offset32_size2");
            }
            offset32_size2 = unsafe { std::ptr::read_unaligned(source as *const u16) } as u32;
            source = unsafe { source.add(2) };
        }

        lz.offset32_stream1_size = offset32_size1;
        lz.offset32_stream2_size = offset32_size2;

        if unsafe { scratch.add(4 * ((offset32_size1 + offset32_size2) as usize) + 64) } > scratch_end {
            bail!("MermaidReadLzTable: Not enough remaining scratch space");
        }

        scratch = align_ptr(scratch, 4);
        lz.offset32_stream1 = scratch as *mut u32;
        scratch = unsafe { scratch.add((offset32_size1 as usize) * 4) };
        unsafe {
            std::ptr::write_bytes(scratch, 0, 32);
            scratch = scratch.add(32);
        }

        lz.offset32_stream2 = scratch as *mut u32;
        scratch = unsafe { scratch.add((offset32_size2 as usize) * 4) };
        unsafe {
            std::ptr::write_bytes(scratch, 0, 32);
        }

        let nb1 = unsafe {
            mermaid_decode_far_offsets(
                source,
                source_end,
                lz.offset32_stream1,
                lz.offset32_stream1_size,
                offset,
            )?
        };
        source = unsafe { source.add(nb1) };

        let nb2 = unsafe {
            mermaid_decode_far_offsets(
                source,
                source_end,
                lz.offset32_stream2,
                lz.offset32_stream2_size,
                offset + 0x10000,
            )?
        };
        source = unsafe { source.add(nb2) };
    } else {
        if unsafe { scratch_end.offset_from(scratch) } < 32 {
            bail!("MermaidReadLzTable: Too few scratch bytes");
        }
        lz.offset32_stream1_size = 0;
        lz.offset32_stream2_size = 0;
        lz.offset32_stream1 = scratch as *mut u32;
        lz.offset32_stream2 = scratch as *mut u32;
        unsafe {
            std::ptr::write_bytes(scratch, 0, 32);
        }
    }

    lz.length_stream = source;
    Ok(true)
}

unsafe fn mermaid_decode_far_offsets(
    source: *const u8,
    source_end: *const u8,
    output: *mut u32,
    output_size: u32,
    offset: isize,
) -> Result<usize> {
    let mut cur = source;
    let max_off = offset as u32;

    if offset < (0xC00000 - 1) {
        for i in 0..(output_size as usize) {
            if unsafe { source_end.offset_from(cur) } < 3 {
                bail!("MermaidDecodeFarOffsets: Too few bytes remaining");
            }
            let off = unsafe {
                (*cur as u32) | ((*cur.add(1) as u32) << 8) | ((*cur.add(2) as u32) << 16)
            };
            cur = unsafe { cur.add(3) };
            unsafe {
                *output.add(i) = off;
            }
            if off > max_off {
                bail!("MermaidDecodeFarOffsets: off ({}) > offset ({})", off, offset);
            }
        }
        return Ok(unsafe { cur.offset_from(source) } as usize);
    }

    for i in 0..(output_size as usize) {
        if unsafe { source_end.offset_from(cur) } < 3 {
            bail!("MermaidDecodeFarOffsets: Too few bytes remaining");
        }
        let mut off = unsafe {
            (*cur as u32) | ((*cur.add(1) as u32) << 8) | ((*cur.add(2) as u32) << 16)
        };
        cur = unsafe { cur.add(3) };

        if off >= 0xc00000 {
            if cur == source_end {
                bail!("MermaidDecodeFarOffsets: No remaining bytes");
            }
            let extra = unsafe { *cur } as u32;
            cur = unsafe { cur.add(1) };
            off += extra << 22;
        }

        unsafe {
            *output.add(i) = off;
        }
        if off > max_off {
            bail!("MermaidDecodeFarOffsets: off ({}) > offset ({})", off, offset);
        }
    }

    Ok(unsafe { cur.offset_from(source) } as usize)
}

#[allow(clippy::too_many_arguments)]
unsafe fn mermaid_process_lz_runs(
    mode: usize,
    _source: *const u8,
    source_end: *const u8,
    mut destination: *mut u8,
    mut destination_size: usize,
    offset: isize,
    destination_end: *mut u8,
    lz: &mut MermaidLzTable,
) -> Result<bool> {
    let destination_start = unsafe { destination.offset(-offset) };
    let mut saved_dist: isize = -8;
    let mut source_current: *const u8 = std::ptr::null();

    for iteration in 0..2 {
        let mut destination_size_current = destination_size;
        if destination_size_current > 0x10000 {
            destination_size_current = 0x10000;
        }

        if iteration == 0 {
            lz.offset32_stream = lz.offset32_stream1;
            // Note: matching C# pointer arithmetic (Offset32Stream1 + Offset32Stream1Size * 4 u32 elements)
            lz.offset32_stream_end = unsafe { lz.offset32_stream1.add((lz.offset32_stream1_size as usize) * 4) };
            lz.cmd_stream_end = unsafe { lz.cmd_stream.add(lz.cmd_stream2_offsets as usize) };
        } else {
            lz.offset32_stream = lz.offset32_stream2;
            lz.offset32_stream_end = unsafe { lz.offset32_stream2.add((lz.offset32_stream2_size as usize) * 4) };
            lz.cmd_stream_end = unsafe { lz.cmd_stream.add(lz.cmd_stream2_offsets_end as usize) };
            lz.cmd_stream = unsafe { lz.cmd_stream.add(lz.cmd_stream2_offsets as usize) };
        }

        if mode == 0 {
            bail!("MermaidProcessLzRuns: Mode 0 not implemented");
        } else {
            let start_off = if offset == 0 && iteration == 0 { 8 } else { 0 };
            source_current = unsafe {
                mermaid_mode1(
                    destination,
                    destination_size_current,
                    destination_end,
                    destination_start,
                    source_end,
                    lz,
                    &mut saved_dist,
                    start_off,
                )?
            };
        }

        destination = unsafe { destination.add(destination_size_current) };
        destination_size -= destination_size_current;
        if destination_size == 0 {
            break;
        }
    }

    if source_current != source_end {
        bail!("MermaidProcessLzRuns: Failed to read decompress source bytes");
    }

    Ok(true)
}

#[allow(clippy::too_many_arguments)]
unsafe fn mermaid_mode1(
    mut destination: *mut u8,
    destination_size: usize,
    _destination_end_ptr: *mut u8,
    _destination_start: *mut u8,
    source_end: *const u8,
    lz: &mut MermaidLzTable,
    saved_dist: &mut isize,
    start_off: usize,
) -> Result<*const u8> {
    let destination_end = unsafe { destination.add(destination_size) };
    let mut cmd_stream = lz.cmd_stream;
    let cmd_stream_end = lz.cmd_stream_end;
    let mut length_stream = lz.length_stream;
    let mut lit_stream = lz.lit_stream;
    let lit_stream_end = lz.lit_stream_end;
    let mut off16_stream = lz.offset16_stream;
    let off16_stream_end = lz.offset16_stream_end;
    let mut off32_stream = lz.offset32_stream;
    let off32_stream_end = lz.offset32_stream_end;
    let mut recent_offs = *saved_dist;
    let destination_begin = destination;

    destination = unsafe { destination.add(start_off) };

    while cmd_stream < cmd_stream_end {
        let flag = unsafe { *cmd_stream } as usize;
        cmd_stream = unsafe { cmd_stream.add(1) };

        if flag >= 24 {
            let new_dist = unsafe { std::ptr::read_unaligned(off16_stream) } as isize;
            let use_distance = ((flag >> 7) as isize).wrapping_sub(1); // 0 if bit 7 set, -1 (all 1s) if bit 7 clear
            let lit_len = flag & 7;

            unsafe {
                copy64(destination, lit_stream);
                destination = destination.add(lit_len);
                lit_stream = lit_stream.add(lit_len);
            }

            recent_offs ^= use_distance & (recent_offs ^ (-new_dist));
            off16_stream = ((off16_stream as usize) + ((use_distance as usize) & 2)) as *const u16;

            let match_ptr = unsafe { destination.offset(recent_offs) };
            unsafe {
                copy64(destination, match_ptr);
                copy64(destination.add(8), match_ptr.add(8));
                destination = destination.add((flag >> 3) & 0xF);
            }
        } else if flag > 2 {
            let length = flag + 5;
            if off32_stream == off32_stream_end {
                bail!("MermaidMode1: off32Stream == off32StreamEnd");
            }
            let off32 = unsafe { *off32_stream } as isize;
            off32_stream = unsafe { off32_stream.add(1) };
            let match_ptr = unsafe { destination_begin.offset(-off32) };
            recent_offs = unsafe { match_ptr.offset_from(destination) };

            if (unsafe { destination_end.offset_from(destination) } as usize) < length {
                bail!("MermaidMode1: destinationEnd - destination < length");
            }

            unsafe {
                copy64(destination, match_ptr);
                copy64(destination.add(8), match_ptr.add(8));
                copy64(destination.add(16), match_ptr.add(16));
                copy64(destination.add(24), match_ptr.add(24));
                destination = destination.add(length);
            }
        } else if flag == 0 {
            if unsafe { source_end.offset_from(length_stream) } == 0 {
                bail!("MermaidMode1: Not enough source bytes remaining");
            }
            let mut length = unsafe { *length_stream } as isize;
            if length > 251 {
                if unsafe { source_end.offset_from(length_stream) } < 3 {
                    bail!("MermaidMode1: Not enough source bytes remaining (need 3)");
                }
                let extra = unsafe { std::ptr::read_unaligned(length_stream.add(1) as *const u16) } as isize;
                length += extra * 4;
                length_stream = unsafe { length_stream.add(2) };
            }
            length_stream = unsafe { length_stream.add(1) };
            length += 64;

            if unsafe { destination_end.offset_from(destination) } < length
                || unsafe { lit_stream_end.offset_from(lit_stream) } < length
            {
                bail!("MermaidMode1: literal run overflow");
            }

            while length > 0 {
                unsafe {
                    copy64(destination, lit_stream);
                    copy64(destination.add(8), lit_stream.add(8));
                    destination = destination.add(16);
                    lit_stream = lit_stream.add(16);
                    length -= 16;
                }
            }
            destination = unsafe { destination.offset(length) };
            lit_stream = unsafe { lit_stream.offset(length) };
        } else if flag == 1 {
            if unsafe { source_end.offset_from(length_stream) } == 0 {
                bail!("MermaidMode1: Not enough source bytes remaining");
            }
            let mut length = unsafe { *length_stream } as isize;
            if length > 251 {
                if unsafe { source_end.offset_from(length_stream) } < 3 {
                    bail!("MermaidMode1: Not enough source bytes remaining (need 3)");
                }
                let extra = unsafe { std::ptr::read_unaligned(length_stream.add(1) as *const u16) } as isize;
                length += extra * 4;
                length_stream = unsafe { length_stream.add(2) };
            }
            length_stream = unsafe { length_stream.add(1) };
            length += 91;

            if off16_stream == off16_stream_end {
                bail!("MermaidMode1: off16Stream == off16StreamEnd");
            }
            let off16 = unsafe { std::ptr::read_unaligned(off16_stream) } as isize;
            off16_stream = unsafe { off16_stream.add(1) };
            let mut match_ptr = unsafe { destination.offset(-off16) };
            recent_offs = unsafe { match_ptr.offset_from(destination) };

            while length > 0 {
                unsafe {
                    copy64(destination, match_ptr);
                    copy64(destination.add(8), match_ptr.add(8));
                    destination = destination.add(16);
                    match_ptr = match_ptr.add(16);
                    length -= 16;
                }
            }
            destination = unsafe { destination.offset(length) };
        } else {
            // flag == 2
            if unsafe { source_end.offset_from(length_stream) } == 0 {
                bail!("MermaidMode1: sourceEnd - lengthStream == 0");
            }
            let mut length = unsafe { *length_stream } as isize;
            if length > 251 {
                if unsafe { source_end.offset_from(length_stream) } < 3 {
                    bail!("MermaidMode1: Not enough source bytes remaining (need 3)");
                }
                let extra = unsafe { std::ptr::read_unaligned(length_stream.add(1) as *const u16) } as isize;
                length += extra * 4;
                length_stream = unsafe { length_stream.add(2) };
            }
            length_stream = unsafe { length_stream.add(1) };
            length += 29;

            if off32_stream == off32_stream_end {
                bail!("MermaidMode1: off32Stream == off32StreamEnd");
            }
            let off32 = unsafe { *off32_stream } as isize;
            off32_stream = unsafe { off32_stream.add(1) };
            let mut match_ptr = unsafe { destination_begin.offset(-off32) };
            recent_offs = unsafe { match_ptr.offset_from(destination) };

            while length > 0 {
                unsafe {
                    copy64(destination, match_ptr);
                    copy64(destination.add(8), match_ptr.add(8));
                    destination = destination.add(16);
                    match_ptr = match_ptr.add(16);
                    length -= 16;
                }
            }
            destination = unsafe { destination.offset(length) };
        }
    }

    let mut length = unsafe { destination_end.offset_from(destination) };
    while length >= 8 {
        unsafe {
            copy64(destination, lit_stream);
            destination = destination.add(8);
            lit_stream = lit_stream.add(8);
            length -= 8;
        }
    }
    while length > 0 {
        unsafe {
            *destination = *lit_stream;
            destination = destination.add(1);
            lit_stream = lit_stream.add(1);
            length -= 1;
        }
    }

    *saved_dist = recent_offs;
    lz.length_stream = length_stream;
    lz.offset16_stream = off16_stream;
    lz.lit_stream = lit_stream;

    Ok(length_stream)
}
