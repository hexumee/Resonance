#pragma once

namespace RuntimeComponent {
    public ref class FingerprintGenerator sealed {
		public:
			static Windows::Foundation::IAsyncOperation<Platform::String^>^ GenerateAsync(const Platform::Array<short>^ monoPcm16);
    };
}
