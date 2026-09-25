(() => {
    // Explicit lookups only. A browser fallback covers hosts that cannot reach
    // Nominatim from their server; keep direct requests below one per second.
    let nextDirectRequestAt = 0;
    let directQueue = Promise.resolve();

    const directLookup = path => {
        const result = directQueue.then(async () => {
            const delay = Math.max(0, nextDirectRequestAt - Date.now());
            if (delay) await new Promise(resolve => setTimeout(resolve, delay));
            nextDirectRequestAt = Date.now() + 1100;
            const response = await fetch(`https://nominatim.openstreetmap.org/${path}`, {
                headers: { 'Accept': 'application/json' },
                referrerPolicy: 'origin'
            });
            if (!response.ok) throw new Error('Không thể tra địa chỉ trực tiếp từ trình duyệt.');
            return response.json();
        });
        directQueue = result.catch(() => {});
        return result;
    };

    const lookup = async (endpoint, directPath) => {
        let serverError;
        try {
            const response = await fetch(`/api/geocoding/${endpoint}`, {
                headers: { 'Accept': 'application/json' }
            });
            const result = await response.json().catch(() => null);
            if (response.ok) return result;
            serverError = result?.message || 'Dịch vụ tìm địa chỉ không phản hồi.';
            if (![502, 503, 504].includes(response.status)) throw new Error(serverError);
        } catch (error) {
            if (!(error instanceof TypeError)) throw error;
            serverError = 'Máy chủ tìm địa chỉ không phản hồi.';
        }

        try {
            return await directLookup(directPath);
        } catch {
            throw new Error(`${serverError} Trình duyệt cũng không kết nối được dịch vụ địa chỉ.`);
        }
    };

    window.SmartCarDeliveryGeocoding = {
        search: query => lookup(
            `search?q=${encodeURIComponent(query)}`,
            `search?format=jsonv2&limit=1&countrycodes=vn&q=${encodeURIComponent(query)}`),
        reverse: (lat, lon) => lookup(
            `reverse?lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}`,
            `reverse?format=jsonv2&addressdetails=1&zoom=18&accept-language=vi&lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}`)
    };
})();
