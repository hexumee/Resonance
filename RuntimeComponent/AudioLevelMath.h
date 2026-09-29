#pragma once

#include <cmath>
#include <algorithm>
#include <cstddef>
#include <stdexcept>

namespace SongRecNative {
	struct AudioLevels { 
		double peak, acRms;
	};

	inline AudioLevels MeasureAudioLevels(const float* values, size_t count, unsigned int channels) {
		if (channels < 1 || channels > 2) {
			throw std::invalid_argument("Expected mono or stereo");
		}

		const size_t perChannel = count / channels;

		double peak = 0, sums[2] = {}, squares[2] = {};
		for (size_t i = 0; i < perChannel * channels; ++i) {
			double value = std::isfinite(values[i]) ? values[i] : 0;
			
			peak = std::max(peak, std::abs(value)); 
			sums[i % channels] += value; 
			squares[i % channels] += value * value;
		}

		double variance = 0;
		if (perChannel) {
			for (unsigned int c = 0; c < channels; ++c) {
				double mean = sums[c] / perChannel;
				variance += std::max(0.0, squares[c] / perChannel - mean * mean);
			}
		}

		return { peak, std::sqrt(variance / channels) };
	}
}
