#pragma once

#include <array>
#include <complex>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace SongRecNative {
	struct Peak { 
		uint32_t pass; 
		uint16_t magnitude, bin; 
	};

	struct Signature {
		uint32_t samples;
		std::array<std::vector<Peak>, 4> bands;
		std::vector<uint8_t> Encode() const;
	};

	Signature Generate(const int16_t* samples, size_t count);
}
