#pragma once

#include <mutex>

namespace RuntimeComponent {
	public ref class AudioLevelReading sealed {
		public:
			property double Peak;
			property double AcRms;
			property unsigned long long Frames;
	};

	public ref class AudioLevelState sealed {
		public:
			AudioLevelState() {}
			AudioLevelReading^ Read();
		internal:
			void Update(double peak, double acRms);
		private:
			std::mutex gate;
			double peak = 0, rms = 0;
			unsigned long long frames = 0;
	};

	public ref class AudioMeter sealed : Windows::Media::Effects::IBasicAudioEffect {
		public:
			AudioMeter() {}
			virtual property bool UseInputFrameForOutput { 
				bool get() { 
					return true; 
				} 
			}
			virtual property Windows::Foundation::Collections::IVectorView<Windows::Media::MediaProperties::AudioEncodingProperties^>^ SupportedEncodingProperties {
				Windows::Foundation::Collections::IVectorView<Windows::Media::MediaProperties::AudioEncodingProperties^>^ get();
			}
			virtual void SetProperties(Windows::Foundation::Collections::IPropertySet^ configuration);
			virtual void SetEncodingProperties(Windows::Media::MediaProperties::AudioEncodingProperties^ encodingProperties);
			virtual void ProcessFrame(Windows::Media::Effects::ProcessAudioFrameContext^ context);
			virtual void Close(Windows::Media::Effects::MediaEffectClosedReason reason) {}
			virtual void DiscardQueuedFrames() {}
		private:
			AudioLevelState^ state;
			unsigned int channels = 1;
	};
}
