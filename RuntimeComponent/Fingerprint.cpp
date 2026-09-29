#include "Fingerprint.h"
#include "Hanning.h"
#include <algorithm>
#include <cmath>
#include <limits>
#include <stdexcept>

namespace SongRecNative {
	namespace {
		void Put(std::vector<uint8_t>& out, uint32_t n, int bytes = 4) {
			for (int i = 0; i < bytes; ++i) {
				out.push_back(static_cast<uint8_t>(n >> (8 * i)));
			}
		}

		void Patch(std::vector<uint8_t>& out, size_t pos, uint32_t n) {
			for (int i = 0; i < 4; ++i) {
				out[pos + i] = static_cast<uint8_t>(n >> (8 * i));
			}
		}

		using Complex = std::complex<float>;

		Complex Rotate(Complex v) {
			return { v.imag(), -v.real() };
		}

		void Butterfly2(Complex& a, Complex& b) {
			const auto difference = a - b;
			a = a + b;
			b = difference;
		}

		void Butterfly4(Complex* a) {
			auto v0 = a[0], v1 = a[1], v2 = a[2], v3 = a[3];

			Butterfly2(v0, v2);
			Butterfly2(v1, v3);

			v3 = Rotate(v3);

			Butterfly2(v0, v1);
			Butterfly2(v2, v3);

			a[0] = v0;
			a[1] = v2;
			a[2] = v1;
			a[3] = v3;
		}

		void Butterfly8(Complex* a) {
			Complex even[4] = { a[0], a[2], a[4], a[6] }, odd[4] = { a[1], a[3], a[5], a[7] };

			Butterfly4(even);
			Butterfly4(odd);

			const float root2 = static_cast<float>(std::sqrt(0.5));

			odd[1] = (Rotate(odd[1]) + odd[1]) * root2;
			odd[2] = Rotate(odd[2]);
			odd[3] = (Rotate(odd[3]) - odd[3]) * root2;

			for (int i = 0; i < 4; ++i) {
				Butterfly2(even[i], odd[i]);

				a[i] = even[i];
				a[i + 4] = odd[i];
			}
		}

		const std::array<Complex, 2048>& Roots() {
			static const auto values = [] {
				std::array<Complex, 2048> result;
				const double constant = -2.0 * 3.14159265358979323846 / 2048.0;

				for (size_t k = 0; k < result.size(); ++k) {
					const double angle = constant * static_cast<double>(k);
					result[k] = { static_cast<float>(std::cos(angle)), static_cast<float>(std::sin(angle)) };
				}

				return result;
			}();

			return values;
		}

		void Butterfly16(Complex* a) {
			Complex even[8], n1[4] = { a[1], a[5], a[9], a[13] }, n3[4] = { a[15], a[3], a[7], a[11] };

			for (int i = 0; i < 8; ++i) {
				even[i] = a[2 * i];
			}

			Butterfly8(even);
			Butterfly4(n1);
			Butterfly4(n3);

			for (int i = 1; i < 4; ++i) {
				const auto w = Roots()[i * 128];
				n1[i] = n1[i] * w;
				n3[i] = n3[i] * std::conj(w);
			}

			for (int i = 0; i < 4; ++i) {
				Butterfly2(n1[i], n3[i]);
				n3[i] = Rotate(n3[i]);

				a[i] = even[i] + n1[i];
				a[i + 4] = even[i + 4] + n3[i];
				a[i + 8] = even[i] - n1[i];
				a[i + 12] = even[i + 4] - n3[i];
			}
		}

		void FFT(std::array<Complex, 2048>& a) {
			// RealFFT packs 2048 real samples into a 1024 point complex transform
			// Match RustFFT's scalar Radix4 plan, including its split-radix base FFT
			std::array<Complex, 1024> reordered;
			for (size_t x = 0; x < 64; ++x) {
				const size_t reversed = (x % 4) * 16 + ((x / 4) % 4) * 4 + x / 16;
				for (size_t y = 0; y < 16; ++y) {
					reordered[y + reversed * 16] = { a[2 * (x + y * 64)].real(), a[2 * (x + y * 64) + 1].real() };
				}
			}

			std::copy(reordered.begin(), reordered.end(), a.begin());
			
			for (size_t i = 0; i < 1024; i += 16) {
				Butterfly16(a.data() + i);
			}
			
			const auto& roots = Roots();
			for (size_t len = 64; len <= 1024; len *= 4) {
				const size_t column = len / 4;

				for (size_t base = 0; base < 1024; base += len) {
					for (size_t j = 0; j < column; ++j) {
						Complex scratch[4] = { a[base + j], 
											   a[base + j + column] * roots[j * (2048 / len)],
											   a[base + j + 2 * column] * roots[2 * j * (2048 / len)],
											   a[base + j + 3 * column] * roots[3 * j * (2048 / len)] 
						};

						Butterfly4(scratch);

						for (size_t k = 0; k < 4; ++k) {
							a[base + j + k * column] = scratch[k];
						}
					}
				}
			}

			const auto first = a[0]; 
			a[0] = { first.real() + first.imag(), 0 }; 
			a[1024] = { first.real() - first.imag(), 0 };

			for (size_t k = 1; k < 512; ++k) {
				const auto sum = a[k] + a[1024 - k], diff = a[k] - a[1024 - k];
				const auto w = roots[k] * 0.5f;
				const float r = sum.imag() * w.real() + diff.real() * w.imag();
				const float im = sum.imag() * w.imag() - diff.real() * w.real();
				const float halfRe = 0.5f * sum.real(), halfIm = 0.5f * diff.imag();

				a[k] = { halfRe + r, halfIm + im }; 
				a[1024 - k] = { halfRe - r, im - halfIm };
			}

			a[512] = { a[512].real(), -a[512].imag() };
		}
	}

	std::vector<uint8_t> Signature::Encode() const {
		std::vector<uint8_t> out;

		Put(out, 0xcafe2580); 
		Put(out, 0); 
		Put(out, 0); 
		Put(out, 0x94119c00);
		Put(out, 0); 
		Put(out, 0); 
		Put(out, 0); 
		Put(out, 3u << 27);
		Put(out, 0); 
		Put(out, 0); 
		Put(out, samples + 3840); 
		Put(out, 0x7c0000);
		Put(out, 0x40000000); 
		Put(out, 0);

		for (size_t band = 0; band < 4; ++band) {
			if (bands[band].empty()) {
				continue;
			}

			std::vector<uint8_t> peaks; 
			uint32_t previous = 0;

			for (const auto& peak : bands[band]) {
				if (peak.pass < previous) {
					throw std::logic_error("Unordered peaks");
				}

				if (peak.pass - previous >= 255) { 
					Put(peaks, 255, 1); 
					Put(peaks, peak.pass); 
					previous = peak.pass;
				}

				Put(peaks, peak.pass - previous, 1); 
				Put(peaks, peak.magnitude, 2); 
				Put(peaks, peak.bin, 2);

				previous = peak.pass;
			}

			Put(out, 0x60030040 + static_cast<uint32_t>(band)); 
			Put(out, static_cast<uint32_t>(peaks.size()));

			out.insert(out.end(), peaks.begin(), peaks.end());

			while (out.size() % 4) {
				out.push_back(0);
			}
		}

		Patch(out, 8, static_cast<uint32_t>(out.size() - 48)); 
		Patch(out, 52, static_cast<uint32_t>(out.size() - 48));

		uint32_t crc = 0xffffffff;
		for (size_t i = 8; i < out.size(); ++i) {
			crc ^= out[i];

			for (int bit = 0; bit < 8; ++bit) {
				crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1)));
			}
		}

		Patch(out, 4, ~crc); 
		
		return out;
	}

	Signature Generate(const int16_t* input, size_t count) {
		if (!input || count == 0 || count > 192000) {
			throw std::invalid_argument("Expected 1..192000 mono PCM16 samples at 16000 Hz");
		}

		Signature signature; 
		signature.samples = static_cast<uint32_t>(count);
		std::array<int16_t, 2048> ring = {};
		std::array<std::complex<float>, 2048> work;

		// Heap allocation is essential (two 1 MB buffers)
		std::vector<std::array<float, 1025>> fft(256), spread(256);

		int ringIndex = 0, index = 0; uint32_t done = 0;
		for (size_t offset = 0; offset + 128 <= count; offset += 128) {
			std::copy(input + offset, input + offset + 128, ring.begin() + ringIndex);
			ringIndex = (ringIndex + 128) & 2047;

			for (int i = 0; i < 2048; ++i) {
				work[i] = std::complex<float>(ring[(i + ringIndex) & 2047] * Hanning[i], 0);
			}

			FFT(work);

			for (int i = 0; i <= 1024; ++i) {
				fft[index][i] = std::max(std::norm(work[i]) / 131072.0f, 1e-10f);
			}

			spread[index] = fft[index];
			for (int i = 0; i <= 1022; ++i) {
				spread[index][i] = std::max(spread[index][i], std::max(spread[index][i + 1], spread[index][i + 2]));
			}

			for (int back : {1, 3, 6}) {
				for (int i = 0; i <= 1024; ++i) {
					spread[(index - back) & 255][i] = std::max(spread[(index - back) & 255][i], spread[index][i]);
				}
			}

			index = (index + 1) & 255;
			++done;

			if (done < 46) {
				continue;
			}

			const auto& current = fft[(index - 46) & 255]; 
			const auto& neighbors = spread[(index - 49) & 255];

			for (int bin = 10; bin <= 1014; ++bin) {
				if (current[bin] < 1.0f / 64 || current[bin] < neighbors[bin - 1]) {
					continue;
				}

				float maximum = 0;
				for (int delta : {-10, -7, -4, -3, 1, 2, 5, 8}) {
					maximum = std::max(maximum, neighbors[bin + delta]);
				}

				if (current[bin] <= maximum) {
					continue;
				}

				for (int delta : {-53, -45, 165, 172, 179, 186, 193, 200, 214, 221, 228, 235, 242, 249}) {
					maximum = std::max(maximum, spread[(index + delta) & 255][bin - 1]);
				}

				if (current[bin] <= maximum) {
					continue;
				}

				auto magnitude = [](float v) { 
					return std::max(std::log(v), 1.0f / 64) * 1477.3f + 6144.0f; 
				};

				float m = magnitude(current[bin]), before = magnitude(current[bin - 1]), after = magnitude(current[bin + 1]);
				float curvature = 2 * m - before - after;

				if (curvature < 0) {
					throw std::logic_error("Negative peak curvature");
				}

				// Rust float-to-i32 casts map NaN to zero and saturate infinities
				// Flat log-floor peaks have 0/0 curvature and must be retained
				const float variation = (after - before) * 32 / curvature;
				const int32_t correction = std::isnan(variation) ? 0 :
										   variation >= 2147483648.0f ? std::numeric_limits<int32_t>::max() :
										   variation <= -2147483648.0f ? std::numeric_limits<int32_t>::min() : static_cast<int32_t>(variation);

				uint16_t corrected = static_cast<uint16_t>(static_cast<uint32_t>(bin * 64) + static_cast<uint32_t>(correction));

				int hz = static_cast<int>(corrected * (8000.0f / 1024 / 64));
				int band = hz >= 250 && hz < 520 ? 0 : hz < 1450 && hz >= 520 ? 1 : hz >= 1450 && hz < 3500 ? 2 : hz >= 3500 && hz <= 5500 ? 3 : -1;

				if (band >= 0) {
					signature.bands[band].push_back({ done - 46, static_cast<uint16_t>(std::min(m, 65535.0f)), corrected });
				}
			}
		}

		return signature;
	}
}
