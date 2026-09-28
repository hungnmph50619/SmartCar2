(() => {
    // Mọi tra cứu địa chỉ đi qua backend SmartCar. Backend chịu trách nhiệm
    // gọi provider bên ngoài, nhờ đó browser không còn phụ thuộc CORS/DNS
    // của Nominatim/Photon và không phát sinh chuỗi "Failed to fetch".
    const requestTimeoutMs = 16000;

    const fetchJson = async (url, options = {}) => {
        const controller = new AbortController();
        const timeoutId = setTimeout(() => controller.abort(), requestTimeoutMs);
        try {
            const response = await fetch(url, { ...options, signal: controller.signal });
            const result = await response.json().catch(() => null);
            return { response, result };
        } finally {
            clearTimeout(timeoutId);
        }
    };

    const hasResults = (result, reverse) => reverse
        ? Boolean(result?.display_name)
        : Array.isArray(result) && result.length > 0;

    const validFocus = focus => {
        const lat = Number(focus?.lat);
        const lon = Number(focus?.lon);
        return Number.isFinite(lat) && lat >= -90 && lat <= 90 &&
            Number.isFinite(lon) && lon >= -180 && lon <= 180
            ? { lat, lon }
            : null;
    };

    const focusQuery = focus => focus
        ? `&nearLat=${encodeURIComponent(focus.lat)}&nearLon=${encodeURIComponent(focus.lon)}`
        : '';

    const lookup = async (endpoint, reverse = false, focus = null) => {
        const focusPoint = validFocus(focus);
        try {
            const { response, result } = await fetchJson(
                `/api/geocoding/${endpoint}${focusQuery(focusPoint)}`,
                { headers: { 'Accept': 'application/json' } });

            if (!response.ok) {
                throw new Error(
                    result?.message ||
                    (reverse
                        ? 'Không thể tìm tên địa chỉ cho vị trí hiện tại.'
                        : 'Không thể tìm địa chỉ lúc này.'));
            }

            if (hasResults(result, reverse)) return result;
            return reverse ? null : [];
        } catch (error) {
            if (error?.name === 'AbortError') {
                throw new Error('Dịch vụ tìm địa chỉ phản hồi quá lâu. Vui lòng thử lại.');
            }

            if (error instanceof TypeError ||
                /failed to fetch|networkerror/i.test(error?.message || '')) {
                throw new Error('Không thể kết nối máy chủ SmartCar để tìm địa chỉ. Vui lòng thử lại.');
            }

            throw error;
        }
    };

    window.SmartCarDeliveryGeocoding = {
        search: (query, focus) =>
            lookup(`search?q=${encodeURIComponent(query)}`, false, focus),

        reverse: (lat, lon) =>
            lookup(
                `reverse?lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}`,
                true)
    };
})();
