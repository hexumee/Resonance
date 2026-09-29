#include "pch.h"
#include "FingerprintGenerator.h"
#include "Fingerprint.h"
#include <memory>
#include <vector>

using namespace Platform;
using namespace Windows::Foundation;
using namespace Windows::Security::Cryptography;
using namespace concurrency;

IAsyncOperation<String^>^ RuntimeComponent::FingerprintGenerator::GenerateAsync(const Array<short>^ pcm) {
	if (pcm == nullptr || pcm->Length == 0 || pcm->Length > 192000) {
		throw ref new InvalidArgumentException(L"Provide up to 12 seconds of mono PCM16 at 16000 Hz.");
	}

    // Snapshot input before dispatch; callers can safely reuse their array
    auto samples = std::make_shared<std::vector<int16_t>>(pcm->Data, pcm->Data+pcm->Length);
    
	return create_async([samples]() -> String^ {
        auto bytes = SongRecNative::Generate(samples->data(), samples->size()).Encode();
        auto array = ref new Array<unsigned char>(bytes.data(), static_cast<unsigned int>(bytes.size()));
        auto base64 = CryptographicBuffer::EncodeToBase64String(CryptographicBuffer::CreateFromByteArray(array));

        return ref new String(L"data:audio/vnd.shazam.sig;base64,") + base64;
    });
}
