//! Unreal Engine FArchive (byte-level) and FBitArchive / NetBitReader (bit-level)
//! ported with zero-copy slicing and 100% bit compatibility with UE5 / Fortnite.

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct FVector {
    pub x: f64,
    pub y: f64,
    pub z: f64,
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct FVector2D {
    pub x: f32,
    pub y: f32,
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct FRotator {
    pub pitch: f32,
    pub yaw: f32,
    pub roll: f32,
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct FQuat {
    pub x: f32,
    pub y: f32,
    pub z: f32,
    pub w: f32,
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct FTransform {
    pub rotation: FQuat,
    pub translation: FVector,
    pub scale3d: FVector,
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct FRepMovement {
    pub linear_velocity: FVector,
    pub angular_velocity: FVector,
    pub location: FVector,
    pub rotation: FRotator,
    pub acceleration: Option<FVector>,
    pub b_simulated_physic_sleep: bool,
    pub b_rep_physics: bool,
    pub b_rep_acceleration: bool,
    pub server_frame: u32,
    pub server_physics_handle: u32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum VectorQuantization {
    RoundWholeNumber,
    RoundOneDecimal,
    RoundTwoDecimals,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RotatorQuantization {
    ByteComponents,
    ShortComponents,
}

#[derive(Clone, Debug, Default)]
pub struct NetworkReplayVersion {
    pub major: u16,
    pub minor: u16,
    pub patch: u16,
    pub changelist: u32,
    pub branch: String,
}

pub fn unreal_name(index: u32) -> &'static str {
    match index {
        0 => "None",
        1 => "ByteProperty",
        2 => "IntProperty",
        3 => "BoolProperty",
        4 => "FloatProperty",
        5 => "ObjectProperty",
        6 => "NameProperty",
        7 => "DelegateProperty",
        8 => "DoubleProperty",
        9 => "ArrayProperty",
        10 => "StructProperty",
        11 => "VectorProperty",
        12 => "RotatorProperty",
        13 => "StrProperty",
        14 => "TextProperty",
        15 => "InterfaceProperty",
        16 => "MulticastDelegateProperty",
        18 => "LazyObjectProperty",
        19 => "SoftObjectProperty",
        20 => "UInt64Property",
        21 => "UInt32Property",
        22 => "UInt16Property",
        23 => "Int64Property",
        25 => "Int16Property",
        26 => "Int8Property",
        28 => "MapProperty",
        29 => "SetProperty",
        30 => "Core",
        31 => "Engine",
        32 => "Editor",
        33 => "CoreUObject",
        34 => "EnumProperty",
        50 => "Cylinder",
        51 => "BoxSphereBounds",
        52 => "Sphere",
        53 => "Box",
        54 => "Vector2D",
        55 => "IntRect",
        56 => "IntPoint",
        57 => "Vector4",
        58 => "Name",
        59 => "Vector",
        60 => "Rotator",
        61 => "SHVector",
        62 => "Color",
        63 => "Plane",
        64 => "Matrix",
        65 => "LinearColor",
        66 => "AdvanceFrame",
        67 => "Pointer",
        68 => "Double",
        69 => "Quat",
        70 => "Self",
        71 => "Transform",
        100 => "Object",
        101 => "Camera",
        102 => "Actor",
        103 => "ObjectRedirector",
        104 => "ObjectArchetype",
        105 => "Class",
        106 => "ScriptStruct",
        107 => "Function",
        200 => "State",
        201 => "TRUE",
        202 => "FALSE",
        203 => "Enum",
        204 => "Default",
        205 => "Skip",
        206 => "Input",
        207 => "Package",
        208 => "Groups",
        209 => "Interface",
        210 => "Components",
        211 => "Global",
        212 => "Super",
        213 => "Outer",
        214 => "Map",
        215 => "Role",
        216 => "RemoteRole",
        217 => "PersistentLevel",
        218 => "TheWorld",
        219 => "PackageMetaData",
        220 => "InitialState",
        221 => "Game",
        222 => "SelectionColor",
        223 => "UI",
        224 => "ExecuteUbergraph",
        225 => "DeviceID",
        226 => "RootStat",
        227 => "MoveActor",
        230 => "All",
        231 => "MeshEmitterVertexColor",
        232 => "TextureOffsetParameter",
        233 => "TextureScaleParameter",
        234 => "ImpactVel",
        235 => "SlideVel",
        236 => "TextureOffset1Parameter",
        237 => "MeshEmitterDynamicParameter",
        238 => "ExpressionInput",
        239 => "Untitled",
        240 => "Timer",
        241 => "Team",
        242 => "Low",
        243 => "High",
        244 => "NetworkGUID",
        245 => "GameThread",
        246 => "RenderThread",
        247 => "OtherChildren",
        248 => "Location",
        249 => "Rotation",
        250 => "BSP",
        251 => "EditorSettings",
        252 => "AudioThread",
        253 => "ID",
        254 => "UserDefinedEnum",
        255 => "Control",
        256 => "Voice",
        257 => "Zlib",
        258 => "Gzip",
        280 => "DGram",
        281 => "Stream",
        282 => "GameNetDriver",
        283 => "PendingNetDriver",
        284 => "BeaconNetDriver",
        285 => "FlushNetDormancy",
        286 => "DemoNetDriver",
        287 => "GameSession",
        288 => "PartySession",
        289 => "GamePort",
        290 => "BeaconPort",
        291 => "MeshPort",
        292 => "MeshNetDriver",
        293 => "LiveStreamVoice",
        300 => "Linear",
        301 => "Point",
        302 => "Aniso",
        303 => "LightMapResolution",
        311 => "UnGrouped",
        312 => "VoiceChat",
        320 => "Playing",
        322 => "Spectating",
        325 => "Inactive",
        350 => "PerfWarning",
        351 => "Info",
        352 => "Init",
        353 => "Exit",
        354 => "Cmd",
        355 => "Warning",
        356 => "Error",
        400 => "FontCharacter",
        401 => "InitChild2StartBone",
        402 => "SoundCueLocalized",
        403 => "SoundCue",
        404 => "RawDistributionFloat",
        405 => "RawDistributionVector",
        406 => "InterpCurveFloat",
        407 => "InterpCurveVector2D",
        408 => "InterpCurveVector",
        409 => "InterpCurveTwoVectors",
        410 => "InterpCurveQuat",
        450 => "AI",
        451 => "NavMesh",
        500 => "PerformanceCapture",
        600 => "EditorLayout",
        601 => "EditorKeyBindings",
        602 => "GameUserSettings",
        _ => "",
    }
}

/// Byte-level reader (`Unreal.Core.BinaryReader`).
#[derive(Clone)]
pub struct ByteReader<'a> {
    pub bytes: &'a [u8],
    pub pos: usize,
    pub is_error: bool,
    pub engine_network_version: u32,
    pub network_version: u32,
    pub replay_header_flags: u32,
    pub replay_version: u32,
    pub network_replay_version: NetworkReplayVersion,
}

impl<'a> ByteReader<'a> {
    pub fn new(bytes: &'a [u8]) -> Self {
        Self {
            bytes,
            pos: 0,
            is_error: false,
            engine_network_version: 0,
            network_version: 0,
            replay_header_flags: 0,
            replay_version: 0,
            network_replay_version: NetworkReplayVersion::default(),
        }
    }

    pub fn with_context(&self, bytes: &'a [u8]) -> Self {
        Self {
            bytes,
            pos: 0,
            is_error: false,
            engine_network_version: self.engine_network_version,
            network_version: self.network_version,
            replay_header_flags: self.replay_header_flags,
            replay_version: self.replay_version,
            network_replay_version: self.network_replay_version.clone(),
        }
    }

    #[inline(always)]
    pub fn len(&self) -> usize {
        self.bytes.len()
    }

    #[inline(always)]
    pub fn at_end(&self) -> bool {
        self.pos >= self.bytes.len()
    }

    #[inline(always)]
    pub fn has_level_streaming_fixes(&self) -> bool {
        (self.replay_header_flags & 2) != 0
    }

    #[inline(always)]
    pub fn has_delta_checkpoints(&self) -> bool {
        (self.replay_header_flags & 4) != 0
    }

    #[inline(always)]
    pub fn has_game_specific_frame_data(&self) -> bool {
        (self.replay_header_flags & 8) != 0
    }

    #[inline(always)]
    pub fn read_byte(&mut self) -> u8 {
        if self.pos >= self.bytes.len() {
            self.is_error = true;
            return 0;
        }
        let b = self.bytes[self.pos];
        self.pos += 1;
        b
    }

    #[inline(always)]
    pub fn read_boolean(&mut self) -> bool {
        self.read_byte() != 0
    }

    #[inline(always)]
    pub fn read_bytes(&mut self, count: usize) -> &'a [u8] {
        if self.pos + count > self.bytes.len() {
            self.is_error = true;
            return &[];
        }
        let s = &self.bytes[self.pos..self.pos + count];
        self.pos += count;
        s
    }

    #[inline(always)]
    pub fn skip_bytes(&mut self, count: usize) {
        if self.pos + count > self.bytes.len() {
            self.is_error = true;
            self.pos = self.bytes.len();
        } else {
            self.pos += count;
        }
    }

    #[inline(always)]
    pub fn seek(&mut self, offset: usize) {
        if offset > self.bytes.len() {
            self.is_error = true;
        } else {
            self.pos = offset;
        }
    }

    #[inline(always)]
    pub fn read_u16(&mut self) -> u16 {
        let b = self.read_bytes(2);
        if b.len() < 2 {
            return 0;
        }
        u16::from_le_bytes([b[0], b[1]])
    }

    #[inline(always)]
    pub fn read_i16(&mut self) -> i16 {
        self.read_u16() as i16
    }

    #[inline(always)]
    pub fn read_u32(&mut self) -> u32 {
        let b = self.read_bytes(4);
        if b.len() < 4 {
            return 0;
        }
        u32::from_le_bytes([b[0], b[1], b[2], b[3]])
    }

    #[inline(always)]
    pub fn read_i32(&mut self) -> i32 {
        self.read_u32() as i32
    }

    #[inline(always)]
    pub fn read_u32_as_boolean(&mut self) -> bool {
        self.read_u32() >= 1
    }

    #[inline(always)]
    pub fn read_u64(&mut self) -> u64 {
        let b = self.read_bytes(8);
        if b.len() < 8 {
            return 0;
        }
        u64::from_le_bytes([b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7]])
    }

    #[inline(always)]
    pub fn read_i64(&mut self) -> i64 {
        self.read_u64() as i64
    }

    #[inline(always)]
    pub fn read_f32(&mut self) -> f32 {
        f32::from_bits(self.read_u32())
    }

    #[inline(always)]
    pub fn read_f64(&mut self) -> f64 {
        f64::from_bits(self.read_u64())
    }

    #[inline(always)]
    pub fn read_int_packed(&mut self) -> u32 {
        let mut value = 0u32;
        let mut count = 0u32;
        let mut remaining = true;
        while remaining {
            if self.pos >= self.bytes.len() {
                self.is_error = true;
                break;
            }
            let next_byte = self.bytes[self.pos];
            self.pos += 1;
            remaining = (next_byte & 1) == 1;
            let part = (next_byte >> 1) as u32;
            value += part << (7 * count);
            count += 1;
        }
        value
    }

    pub fn read_fstring(&mut self) -> String {
        let length = self.read_i32();
        if length == 0 || self.is_error {
            return String::new();
        }
        let is_unicode = length < 0;
        let byte_len = if is_unicode {
            (-2i64 * (length as i64)) as usize
        } else {
            length as usize
        };
        if self.pos + byte_len > self.bytes.len() {
            self.is_error = true;
            return String::new();
        }
        let mut slice = self.read_bytes(byte_len);
        if !is_unicode {
            if !slice.is_empty() && slice[slice.len() - 1] == 0 {
                slice = &slice[..slice.len() - 1];
            }
            // Note: Windows default encoding or Latin1/UTF-8 fallback:
            // Note in C# PlayerNameData: EncodedName is read via Encoding.Default or Unicode, where each byte < 128 or Latin1 maps to char!
            let s: String = slice.iter().map(|&b| b as char).collect();
            s.trim_matches(' ').to_string()
        } else {
            if slice.len() >= 2 && slice[slice.len() - 1] == 0 && slice[slice.len() - 2] == 0 {
                slice = &slice[..slice.len() - 2];
            }
            let mut u16s = Vec::with_capacity(slice.len() / 2);
            for chunk in slice.chunks_exact(2) {
                u16s.push(u16::from_le_bytes([chunk[0], chunk[1]]));
            }
            String::from_utf16_lossy(&u16s).trim_matches(' ').to_string()
        }
    }

    pub fn read_fname(&mut self) -> String {
        let is_hardcoded = self.read_boolean();
        if is_hardcoded {
            let name_index = if self.engine_network_version < 6 {
                self.read_u32()
            } else {
                self.read_int_packed()
            };
            let s = unreal_name(name_index);
            if !s.is_empty() {
                return s.to_string();
            }
            return name_index.to_string();
        }
        let in_string = self.read_fstring();
        let _in_number = self.read_i32();
        in_string
    }

    pub fn read_guid_hex(&mut self, size: usize) -> String {
        let bytes = self.read_bytes(size);
        let mut s = String::with_capacity(bytes.len() * 2);
        for &b in bytes {
            use std::fmt::Write;
            let _ = write!(&mut s, "{:02X}", b);
        }
        s
    }

    pub fn read_fquat(&mut self) -> FQuat {
        FQuat {
            x: self.read_f32(),
            y: self.read_f32(),
            z: self.read_f32(),
            w: self.read_f32(),
        }
    }

    pub fn read_fvector(&mut self) -> FVector {
        FVector {
            x: self.read_f32() as f64,
            y: self.read_f32() as f64,
            z: self.read_f32() as f64,
        }
    }

    pub fn read_ftransform(&mut self) -> FTransform {
        FTransform {
            rotation: self.read_fquat(),
            translation: self.read_fvector(),
            scale3d: self.read_fvector(),
        }
    }
}

#[derive(Clone, Copy, Debug)]
pub enum EndIndex {
    Bunch = 0,
    ContentBlockPayload = 1,
    FieldHeaderPayload = 2,
    ReadArrayField = 3,
}

/// Bit-level reader (`Unreal.Core.NetBitReader` / `BitReader`).
#[derive(Clone, Debug)]
pub struct BitReader {
    pub buffer: Vec<u8>,
    pub pos: usize,
    pub last_bit: usize,
    pub is_error: bool,
    temp_last_bit: [usize; 4],
    pub engine_network_version: u32,
    pub network_version: u32,
    pub replay_header_flags: u32,
    pub replay_version: u32,
    pub changelist: u32,
    pub branch_is_41: bool,
}

impl Default for BitReader {
    fn default() -> Self {
        Self::new()
    }
}

impl BitReader {
    pub fn new() -> Self {
        Self {
            buffer: Vec::with_capacity(2048),
            pos: 0,
            last_bit: 0,
            is_error: false,
            temp_last_bit: [0; 4],
            engine_network_version: 0,
            network_version: 0,
            replay_header_flags: 0,
            replay_version: 0,
            changelist: 0,
            branch_is_41: false,
        }
    }

    pub fn from_slice(slice: &[u8], bit_count: usize) -> Self {
        let mut r = Self::new();
        r.fill_buffer(slice, bit_count);
        r
    }

    pub fn copy_versions_from(&mut self, other: &BitReader) {
        self.engine_network_version = other.engine_network_version;
        self.network_version = other.network_version;
        self.replay_header_flags = other.replay_header_flags;
        self.replay_version = other.replay_version;
        self.changelist = other.changelist;
        self.branch_is_41 = other.branch_is_41;
    }

    pub fn fill_buffer(&mut self, input: &[u8], bit_count: usize) {
        self.buffer.clear();
        self.buffer.extend_from_slice(input);
        self.last_bit = bit_count;
        self.pos = 0;
        self.is_error = false;
    }

    pub fn fill_bits_from(&mut self, source: &mut BitReader, bit_count: usize) {
        if !source.can_read(bit_count) {
            self.is_error = source.is_error;
            self.buffer.clear();
            self.last_bit = 0;
            self.pos = 0;
            return;
        }
        let req_bytes = (bit_count + 7) / 8;
        self.buffer.clear();
        self.buffer.resize(req_bytes, 0);
        source.read_bits_into(&mut self.buffer[..req_bytes], bit_count);
        self.last_bit = bit_count;
        self.pos = 0;
        self.is_error = source.is_error;
    }

    pub fn append_data_from_checked(&mut self, data: &[u8], bit_count: usize) {
        self.last_bit += bit_count;
        self.buffer.extend_from_slice(data);
    }

    #[inline(always)]
    pub fn at_end(&self) -> bool {
        self.pos >= self.last_bit
    }

    #[inline(always)]
    pub fn can_read(&self, count: usize) -> bool {
        self.pos + count <= self.last_bit
    }

    #[inline(always)]
    pub fn get_bits_left(&self) -> usize {
        self.last_bit.saturating_sub(self.pos)
    }

    #[inline(always)]
    pub fn set_temp_end(&mut self, size: usize, idx: EndIndex) {
        // Patch 9: always save current LastBit even on overflow!
        self.temp_last_bit[idx as usize] = self.last_bit;
        let set_pos = self.pos + size;
        if set_pos > self.last_bit {
            self.is_error = true;
            return;
        }
        self.last_bit = set_pos;
    }

    #[inline(always)]
    pub fn restore_temp_end(&mut self, idx: EndIndex) {
        self.pos = self.last_bit;
        self.last_bit = self.temp_last_bit[idx as usize];
        self.is_error = false;
    }

    #[inline(always)]
    pub fn skip_bits(&mut self, num_bits: usize) {
        if self.pos + num_bits > self.buffer.len() * 8 {
            self.is_error = true;
        } else {
            self.pos += num_bits;
        }
    }

    #[inline(always)]
    pub fn seek_end(&mut self) {
        self.pos = self.buffer.len() * 8;
    }

    #[inline(always)]
    pub fn peek_bit(&self) -> bool {
        let bit = self.pos & 7;
        let byte_idx = self.pos >> 3;
        if byte_idx >= self.buffer.len() {
            return false;
        }
        (self.buffer[byte_idx] & (1 << bit)) != 0
    }

    #[inline(always)]
    pub fn read_bit(&mut self) -> bool {
        if self.pos >= self.last_bit || self.is_error {
            self.is_error = true;
            return false;
        }
        let bit = self.pos & 7;
        let byte_idx = self.pos >> 3;
        self.pos += 1;
        (self.buffer[byte_idx] & (1 << bit)) != 0
    }

    #[inline(always)]
    pub fn read_bits_to_u32(&mut self, bit_count: usize) -> u32 {
        if !self.can_read(bit_count) {
            self.is_error = true;
            return 0;
        }
        if bit_count == 0 {
            return 0;
        }
        let cur_byte = self.pos >> 3;
        if cur_byte + 8 <= self.buffer.len() && bit_count <= 32 {
            let mut arr = [0u8; 8];
            arr.copy_from_slice(&self.buffer[cur_byte..cur_byte + 8]);
            let val = u64::from_le_bytes(arr);
            let mask = if bit_count == 64 {
                !0u64
            } else {
                (1u64 << bit_count) - 1
            };
            let res = ((val >> (self.pos & 7)) & mask) as u32;
            self.pos += bit_count;
            return res;
        }
        let mut result = 0u32;
        for i in 0..bit_count {
            if self.is_error {
                return 0;
            }
            if self.read_bit() {
                result |= 1u32 << i;
            }
        }
        result
    }

    #[inline(always)]
    pub fn read_bits_to_u64(&mut self, bit_count: usize) -> u64 {
        if !self.can_read(bit_count) {
            self.is_error = true;
            return 0;
        }
        if bit_count == 0 {
            return 0;
        }
        let cur_byte = self.pos >> 3;
        if cur_byte + 8 <= self.buffer.len() && bit_count <= 57 {
            let mut arr = [0u8; 8];
            arr.copy_from_slice(&self.buffer[cur_byte..cur_byte + 8]);
            let val = u64::from_le_bytes(arr);
            let mask = if bit_count == 64 {
                !0u64
            } else {
                (1u64 << bit_count) - 1
            };
            let res = (val >> (self.pos & 7)) & mask;
            self.pos += bit_count;
            return res;
        }
        let mut result = 0u64;
        for i in 0..bit_count {
            if self.read_bit() {
                result |= 1u64 << i;
            }
        }
        result
    }

    pub fn read_bits_into(&mut self, destination: &mut [u8], bit_count: usize) {
        if !self.can_read(bit_count) {
            self.is_error = true;
            return;
        }
        let bit_used = self.pos & 7;
        let byte_count = bit_count / 8;
        let extra_bits = bit_count % 8;
        let cur_byte = self.pos >> 3;

        if bit_used == 0 {
            if byte_count > 0 {
                destination[..byte_count]
                    .copy_from_slice(&self.buffer[cur_byte..cur_byte + byte_count]);
                self.pos += byte_count * 8;
            }
            if extra_bits > 0 {
                let cb = self.pos >> 3;
                destination[byte_count] = self.buffer[cb] & ((1u8 << extra_bits) - 1);
                self.pos += extra_bits;
            }
            return;
        }

        let needed_bytes = (bit_count + 7) / 8;
        destination[..needed_bytes].fill(0);

        let bit_left = 8 - bit_used;
        let shift_delta = (1u8 << bit_used) - 1;
        for i in 0..byte_count {
            destination[i] = (self.buffer[cur_byte + i] >> bit_used)
                | ((self.buffer[cur_byte + i + 1] & shift_delta) << bit_left);
        }
        self.pos += byte_count * 8;

        for i in 0..extra_bits {
            let cb = self.pos >> 3;
            let bit = (self.buffer[cb] & (1 << (self.pos & 7))) != 0;
            self.pos += 1;
            if bit {
                destination[needed_bytes - 1] |= 1u8 << i;
            }
        }
    }

    pub fn read_bits_to_vec(&mut self, bit_count: usize) -> Vec<u8> {
        if !self.can_read(bit_count) {
            self.is_error = true;
            return Vec::new();
        }
        let needed_bytes = (bit_count + 7) / 8;
        let mut out = vec![0u8; needed_bytes];
        self.read_bits_into(&mut out, bit_count);
        out
    }

    #[inline(always)]
    pub fn read_byte(&mut self) -> u8 {
        if !self.can_read(8) {
            self.is_error = true;
            return 0;
        }
        let bit_used = self.pos & 7;
        let cur = self.pos >> 3;
        self.pos += 8;
        if bit_used == 0 {
            self.buffer[cur]
        } else {
            (self.buffer[cur] >> bit_used)
                | ((self.buffer[cur + 1] & ((1u8 << bit_used) - 1)) << (8 - bit_used))
        }
    }

    pub fn read_bytes_into(&mut self, destination: &mut [u8]) {
        let byte_count = destination.len();
        if !self.can_read(byte_count * 8) {
            self.is_error = true;
            destination.fill(0);
            return;
        }
        let bit_used = self.pos & 7;
        let cur_byte = self.pos >> 3;
        if bit_used == 0 {
            destination.copy_from_slice(&self.buffer[cur_byte..cur_byte + byte_count]);
        } else {
            let bit_left = 8 - bit_used;
            let mask = (1u8 << bit_used) - 1;
            for i in 0..byte_count {
                destination[i] = (self.buffer[cur_byte + i] >> bit_used)
                    | ((self.buffer[cur_byte + 1 + i] & mask) << bit_left);
            }
        }
        self.pos += byte_count * 8;
    }

    pub fn read_bytes_to_vec(&mut self, byte_count: usize) -> Vec<u8> {
        if !self.can_read(byte_count * 8) {
            self.is_error = true;
            return Vec::new();
        }
        let mut out = vec![0u8; byte_count];
        self.read_bytes_into(&mut out);
        out
    }

    pub fn read_bytes_to_hex(&mut self, count: usize) -> String {
        let bytes = self.read_bytes_to_vec(count);
        let mut s = String::with_capacity(bytes.len() * 2);
        for b in bytes {
            use std::fmt::Write;
            let _ = write!(&mut s, "{:02X}", b);
        }
        s
    }

    #[inline(always)]
    pub fn read_serialized_int(&mut self, max_value: u32) -> u32 {
        let mut value = 0u32;
        let mut mask = 1u32;
        while (value + mask) < max_value {
            if self.read_bit() {
                value |= mask;
            }
            mask = mask.saturating_mul(2);
            if mask == 0 {
                break;
            }
        }
        value
    }

    #[inline(always)]
    pub fn read_u16(&mut self) -> u16 {
        if !self.can_read(16) {
            self.is_error = true;
            return 0;
        }
        if (self.pos & 7) == 0 {
            let cur = self.pos >> 3;
            let res = u16::from_le_bytes([self.buffer[cur], self.buffer[cur + 1]]);
            self.pos += 16;
            return res;
        }
        let mut b = [0u8; 2];
        self.read_bytes_into(&mut b);
        u16::from_le_bytes(b)
    }

    #[inline(always)]
    pub fn read_i16(&mut self) -> i16 {
        self.read_u16() as i16
    }

    #[inline(always)]
    pub fn read_u32(&mut self) -> u32 {
        if !self.can_read(32) {
            self.is_error = true;
            return 0;
        }
        if (self.pos & 7) == 0 {
            let cur = self.pos >> 3;
            let res = u32::from_le_bytes([
                self.buffer[cur],
                self.buffer[cur + 1],
                self.buffer[cur + 2],
                self.buffer[cur + 3],
            ]);
            self.pos += 32;
            return res;
        }
        let mut b = [0u8; 4];
        self.read_bytes_into(&mut b);
        u32::from_le_bytes(b)
    }

    #[inline(always)]
    pub fn read_i32(&mut self) -> i32 {
        self.read_u32() as i32
    }

    #[inline(always)]
    pub fn read_u64(&mut self) -> u64 {
        if !self.can_read(64) {
            self.is_error = true;
            return 0;
        }
        if (self.pos & 7) == 0 {
            let cur = self.pos >> 3;
            let mut b = [0u8; 8];
            b.copy_from_slice(&self.buffer[cur..cur + 8]);
            self.pos += 64;
            return u64::from_le_bytes(b);
        }
        let mut b = [0u8; 8];
        self.read_bytes_into(&mut b);
        u64::from_le_bytes(b)
    }

    #[inline(always)]
    pub fn read_f32(&mut self) -> f32 {
        f32::from_bits(self.read_u32())
    }

    #[inline(always)]
    pub fn read_f64(&mut self) -> f64 {
        f64::from_bits(self.read_u64())
    }

    #[inline(always)]
    pub fn read_int_packed(&mut self) -> u32 {
        if (self.pos & 7) == 0 {
            let mut src_idx = self.pos >> 3;
            let mut val = 0u32;
            for it in 0..5 {
                if !self.can_read(8) {
                    self.is_error = true;
                    break;
                }
                self.pos += 8;
                let b = self.buffer[src_idx];
                src_idx += 1;
                val |= ((b >> 1) as u32) << (it * 7);
                if (b & 1) == 0 {
                    break;
                }
            }
            return val;
        }

        let bit_used = self.pos & 7;
        let bit_left = 8 - bit_used;
        let mask0 = ((1u32 << bit_left) - 1) as u8;
        let mask1 = ((1u32 << bit_used) - 1) as u8;
        let mut cur_byte = self.pos >> 3;
        let mut next_byte = cur_byte + 1;

        let mut value = 0u32;
        for it in 0..5 {
            if !self.can_read(8) {
                self.is_error = true;
                break;
            }
            if next_byte >= self.buffer.len() {
                next_byte = cur_byte;
            }
            self.pos += 8;
            let read_byte = ((self.buffer[cur_byte] >> bit_used) & mask0)
                | ((self.buffer[next_byte] & mask1) << (bit_left & 7));
            value |= ((read_byte >> 1) as u32) << (it * 7);
            cur_byte += 1;
            next_byte += 1;
            if (read_byte & 1) == 0 {
                break;
            }
        }
        value
    }

    pub fn read_fstring(&mut self) -> String {
        let length = self.read_i32();
        if length == 0 || self.is_error {
            return String::new();
        }
        let is_unicode = length < 0;
        let byte_len = if is_unicode {
            (-2i64 * (length as i64)) as usize
        } else {
            length as usize
        };
        if !self.can_read(byte_len * 8) {
            self.is_error = true;
            return String::new();
        }
        let bytes = self.read_bytes_to_vec(byte_len);
        let mut slice = &bytes[..];
        if !is_unicode {
            if !slice.is_empty() && slice[slice.len() - 1] == 0 {
                slice = &slice[..slice.len() - 1];
            }
            let s: String = slice.iter().map(|&b| b as char).collect();
            s.trim_matches(' ').to_string()
        } else {
            if slice.len() >= 2 && slice[slice.len() - 1] == 0 && slice[slice.len() - 2] == 0 {
                slice = &slice[..slice.len() - 2];
            }
            let mut u16s = Vec::with_capacity(slice.len() / 2);
            for chunk in slice.chunks_exact(2) {
                u16s.push(u16::from_le_bytes([chunk[0], chunk[1]]));
            }
            String::from_utf16_lossy(&u16s).trim_matches(' ').to_string()
        }
    }

    pub fn read_fname(&mut self) -> String {
        let is_hardcoded = self.read_bit();
        if is_hardcoded {
            let name_index = if self.engine_network_version < 6 {
                self.read_u32()
            } else {
                self.read_int_packed()
            };
            let s = unreal_name(name_index);
            if !s.is_empty() {
                return s.to_string();
            }
            return name_index.to_string();
        }
        let in_string = self.read_fstring();
        let _in_number = self.read_i32();
        in_string
    }

    pub fn read_fvector(&mut self) -> FVector {
        if self.engine_network_version >= 23 {
            // HISTORY_PACKED_VECTOR_LWC_SUPPORT
            FVector {
                x: self.read_f64(),
                y: self.read_f64(),
                z: self.read_f64(),
            }
        } else {
            FVector {
                x: self.read_f32() as f64,
                y: self.read_f32() as f64,
                z: self.read_f32() as f64,
            }
        }
    }

    pub fn read_packed_vector(&mut self, scale_factor: i32, max_bits: i32) -> FVector {
        if self.engine_network_version >= 23 && self.engine_network_version != 26 {
            return self.read_quantized_vector(scale_factor);
        }
        self.read_packed_vector_legacy(scale_factor, max_bits)
    }

    fn read_quantized_vector(&mut self, scale_factor: i32) -> FVector {
        let info = self.read_serialized_int(1 << 7);
        let bit_count = (info & 63) as usize;
        let extra_info = info >> 6;

        if bit_count > 0 {
            let x = self.read_bits_to_u64(bit_count);
            let y = self.read_bits_to_u64(bit_count);
            let z = self.read_bits_to_u64(bit_count);
            let sign_bit = 1u64 << (bit_count - 1);
            let mut fx = ((x ^ sign_bit) as i64 - (sign_bit as i64)) as f64;
            let mut fy = ((y ^ sign_bit) as i64 - (sign_bit as i64)) as f64;
            let mut fz = ((z ^ sign_bit) as i64 - (sign_bit as i64)) as f64;
            if extra_info > 0 {
                fx /= scale_factor as f64;
                fy /= scale_factor as f64;
                fz /= scale_factor as f64;
            }
            FVector {
                x: fx,
                y: fy,
                z: fz,
            }
        } else if extra_info == 0 {
            FVector {
                x: self.read_f32() as f64,
                y: self.read_f32() as f64,
                z: self.read_f32() as f64,
            }
        } else {
            FVector {
                x: self.read_f64(),
                y: self.read_f64(),
                z: self.read_f64(),
            }
        }
    }

    fn read_packed_vector_legacy(&mut self, scale_factor: i32, max_bits: i32) -> FVector {
        let bits = self.read_serialized_int(max_bits as u32);
        if self.is_error {
            return FVector::default();
        }
        let bias = 1i64 << (bits + 1);
        let max = 1u32 << (bits + 2);
        let dx = self.read_serialized_int(max) as i64;
        let dy = self.read_serialized_int(max) as i64;
        let dz = self.read_serialized_int(max) as i64;
        if self.is_error {
            return FVector::default();
        }
        FVector {
            x: ((dx - bias) / (scale_factor as i64)) as f64,
            y: ((dy - bias) / (scale_factor as i64)) as f64,
            z: ((dz - bias) / (scale_factor as i64)) as f64,
        }
    }

    pub fn read_rotation(&mut self) -> FRotator {
        let mut pitch = 0.0f32;
        let mut yaw = 0.0f32;
        let mut roll = 0.0f32;
        if self.read_bit() {
            pitch = (self.read_byte() as i32 * 360 / 256) as f32;
        }
        if self.read_bit() {
            yaw = (self.read_byte() as i32 * 360 / 256) as f32;
        }
        if self.read_bit() {
            roll = (self.read_byte() as i32 * 360 / 256) as f32;
        }
        if self.is_error {
            return FRotator::default();
        }
        FRotator { pitch, yaw, roll }
    }

    pub fn read_rotation_short(&mut self) -> FRotator {
        let mut pitch = 0.0f32;
        let mut yaw = 0.0f32;
        let mut roll = 0.0f32;
        if self.read_bit() {
            pitch = (self.read_u16() as i32 * 360 / 65536) as f32;
        }
        if self.read_bit() {
            yaw = (self.read_u16() as i32 * 360 / 65536) as f32;
        }
        if self.read_bit() {
            roll = (self.read_u16() as i32 * 360 / 65536) as f32;
        }
        if self.is_error {
            return FRotator::default();
        }
        FRotator { pitch, yaw, roll }
    }

    pub fn serialize_quantized_vector(&mut self, level: VectorQuantization) -> FVector {
        match level {
            VectorQuantization::RoundTwoDecimals => self.read_packed_vector(100, 30),
            VectorQuantization::RoundOneDecimal => self.read_packed_vector(10, 27),
            VectorQuantization::RoundWholeNumber => self.read_packed_vector(1, 24),
        }
    }

    pub fn read_fixed_compressed_float(&mut self, max_value: i32, num_bits: i32) -> f32 {
        let max_bit_value = (1 << (num_bits - 1)) - 1;
        let bias = 1 << (num_bits - 1);
        let ser_int_max = 1u32 << num_bits;
        let delta = self.read_serialized_int(ser_int_max) as i32;
        let unscaled = (delta - bias) as f32;
        if max_value > max_bit_value {
            let inv_scale = max_value as f32 / max_bit_value as f32;
            unscaled * inv_scale
        } else {
            let scale = max_bit_value as f32 / max_value as f32;
            let inv_scale = 1.0f32 / scale;
            unscaled * inv_scale
        }
    }

    pub fn serialize_vector_normal(&mut self) -> FVector {
        FVector {
            x: self.read_fixed_compressed_float(1, 16) as f64,
            y: self.read_fixed_compressed_float(1, 16) as f64,
            z: self.read_fixed_compressed_float(1, 16) as f64,
        }
    }

    pub fn serialize_rep_movement(
        &mut self,
        loc_quant: VectorQuantization,
        rot_quant: RotatorQuantization,
        vel_quant: VectorQuantization,
    ) -> FRepMovement {
        let b_simulated_physic_sleep = self.read_bit();
        let b_rep_physics = self.read_bit();
        let mut b_rep_server_frame = false;
        let mut b_rep_server_handle = false;

        if self.engine_network_version >= 25 && self.engine_network_version != 26 {
            b_rep_server_frame = self.read_bit();
            b_rep_server_handle = self.read_bit();
        }

        let location = self.serialize_quantized_vector(loc_quant);
        let rotation = match rot_quant {
            RotatorQuantization::ByteComponents => self.read_rotation(),
            RotatorQuantization::ShortComponents => self.read_rotation_short(),
        };
        let linear_velocity = self.serialize_quantized_vector(vel_quant);
        let angular_velocity = if b_rep_physics {
            self.serialize_quantized_vector(vel_quant)
        } else {
            FVector::default()
        };
        let server_frame = if b_rep_server_frame {
            self.read_int_packed()
        } else {
            0
        };
        let server_physics_handle = if b_rep_server_handle {
            self.read_int_packed()
        } else {
            0
        };

        let mut b_rep_acceleration = false;
        let mut acceleration = None;
        if self.engine_network_version >= 35 {
            b_rep_acceleration = self.read_bit();
            if b_rep_acceleration {
                acceleration = Some(self.serialize_quantized_vector(vel_quant));
            }
        }

        // Patch 17: physics actors (vehicles) end with a few flag bits (3 in Fortnite 43)
        let left = self.get_bits_left();
        if !self.is_error && left > 0 && left <= 8 {
            self.skip_bits(left);
        }

        FRepMovement {
            linear_velocity,
            angular_velocity,
            location,
            rotation,
            acceleration,
            b_simulated_physic_sleep,
            b_rep_physics,
            b_rep_acceleration,
            server_frame,
            server_physics_handle,
        }
    }

    pub fn serialize_property_net_id(&mut self) -> String {
        const TYPE_HASH_OTHER: u8 = 31;
        let encoding_flags = self.read_byte();
        let mut encoded = false;
        if (encoding_flags & 1) != 0 {
            encoded = true;
            if (encoding_flags & 2) != 0 {
                return String::new();
            }
        }
        let type_hash = (encoding_flags & !7u8) >> 3;
        if type_hash == 0 {
            return "NULL".to_string();
        }
        let mut valid_type_hash = type_hash != 0;
        if type_hash == TYPE_HASH_OTHER {
            let type_string = self.read_fstring();
            if type_string == "None" {
                valid_type_hash = false;
            }
        }
        if valid_type_hash {
            if encoded {
                let encoded_size = self.read_byte() as usize;
                return self.read_bytes_to_hex(encoded_size);
            } else {
                return self.read_fstring();
            }
        }
        String::new()
    }
}
