#include "pch.h"
#include "AudioMeter.h"
#include "AudioLevelMath.h"
#include <MemoryBuffer.h>
#include <wrl.h>
#include <algorithm>

using namespace Windows::Media;
using namespace Windows::Media::MediaProperties;
using namespace Windows::Foundation::Collections;

namespace RuntimeComponent {
	AudioLevelReading^ AudioLevelState::Read() {
		std::lock_guard<std::mutex> lock(gate);

		auto result = ref new AudioLevelReading(); 
		result->Peak = peak; 
		result->AcRms = rms; 
		result->Frames = frames; 
		peak = 0;
		
		return result;
	}

	void AudioLevelState::Update(double p, double r) {
		std::lock_guard<std::mutex> lock(gate); 

		peak = std::max(peak, p); 
		rms = r; 
		++frames;
	}

	IVectorView<AudioEncodingProperties^>^ AudioMeter::SupportedEncodingProperties::get() {
		auto result = ref new Platform::Collections::Vector<AudioEncodingProperties^>();
		
		for (unsigned int rate : { 16000u, 44100u, 48000u }) {
			for (unsigned int channels : { 1u, 2u }) {
				auto properties = AudioEncodingProperties::CreatePcm(rate, channels, 32);

				properties->Subtype = MediaEncodingSubtypes::Float; 
				result->Append(properties);
			}
		}

		return result->GetView();
	}

	void AudioMeter::SetProperties(IPropertySet^ properties) {
		if (properties->HasKey("Meter")) {
			state = dynamic_cast<AudioLevelState^>(properties->Lookup("Meter"));
		}
	}

	void AudioMeter::SetEncodingProperties(AudioEncodingProperties^ properties) {
		if (properties->Subtype != MediaEncodingSubtypes::Float || properties->BitsPerSample != 32 || properties->ChannelCount < 1 || properties->ChannelCount > 2) {
			throw ref new Platform::InvalidArgumentException("AudioMeter requires float32 audio");
		}

		channels = properties->ChannelCount;
	}

	void AudioMeter::ProcessFrame(Windows::Media::Effects::ProcessAudioFrameContext^ context) {
		if (state == nullptr) {
			return;
		}

		auto buffer = context->InputFrame->LockBuffer(AudioBufferAccessMode::Read);
		auto reference = buffer->CreateReference();

		Microsoft::WRL::ComPtr<Windows::Foundation::IMemoryBufferByteAccess> access;
		auto unknown = reinterpret_cast<IUnknown*>(reference);
		HRESULT hr = unknown->QueryInterface(IID_PPV_ARGS(&access));
		if (FAILED(hr)) {
			delete reference; 
			delete buffer; 
			
			return;
		}

		BYTE* bytes = nullptr;
		UINT32 capacity = 0;
		hr = access->GetBuffer(&bytes, &capacity);
		if (SUCCEEDED(hr) && bytes != nullptr) {
			const size_t count = std::min(capacity, buffer->Length) / sizeof(float);
			const auto values = reinterpret_cast<const float*>(bytes);

			if (count >= channels) { 
				auto levels = SongRecNative::MeasureAudioLevels(values, count, channels); 
				state->Update(levels.peak, levels.acRms); 
			}
		}

		// The frame remains untouched and is reused as output by the media pipeline
		delete reference; 
		delete buffer;
	}
}
