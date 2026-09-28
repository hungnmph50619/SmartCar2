(() => {
    // Explicit lookups only. A browser fallback covers hosts that cannot reach
    // Nominatim from their server; keep direct requests below one per second.
    let nextDirectRequestAt = 0;
    let directQueue = Promise.resolve();
    const requestTimeoutMs = 8000;

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

    const appendViewbox = (path, focus) => {
        if (!focus) return path;
        const box = [focus.lon - 0.55, focus.lat - 0.45, focus.lon + 0.55, focus.lat + 0.45]
            .map(value => value.toFixed(5)).join(',');
        return `${path}&viewbox=${encodeURIComponent(box)}`;
    };

    const focusQuery = focus => focus
        ? `&nearLat=${encodeURIComponent(focus.lat)}&nearLon=${encodeURIComponent(focus.lon)}`
        : '';

    const directLookup = path => {
        const result = directQueue.then(async () => {
            const delay = Math.max(0, nextDirectRequestAt - Date.now());
            if (delay) await new Promise(resolve => setTimeout(resolve, delay));
            nextDirectRequestAt = Date.now() + 1100;
            const { response, result } = await fetchJson(`https://nominatim.openstreetmap.org/${path}`, {
                headers: { 'Accept': 'application/json' },
                referrerPolicy: 'origin'
            });
            if (!response.ok) throw new Error('Không thể tra địa chỉ trực tiếp từ trình duyệt.');
            return result;
        });
        directQueue = result.catch(() => {});
        return result;
    };

    const photonLookup = async (path, reverse) => {
        const { response, result: data } = await fetchJson(`https://photon.komoot.io/${path}`, {
            headers: { 'Accept': 'application/json' },
            referrerPolicy: 'origin'
        });
        if (!response.ok) throw new Error('Dịch vụ địa chỉ dự phòng không phản hồi.');
        const matches = (data.features || []).map(feature => {
            const [lon, lat] = feature.geometry?.coordinates || [];
            const properties = feature.properties || {};
            const streetAddress = [properties.housenumber, properties.street].filter(Boolean).join(' ');
            const display_name = [properties.name, streetAddress, properties.district,
                properties.city, properties.state, properties.country]
                .filter((part, index, parts) => part && parts.indexOf(part) === index)
                .join(', ');
            return { lat: String(lat), lon: String(lon), display_name, countrycode: properties.countrycode };
        }).filter(result => Number.isFinite(Number(result.lat)) &&
            Number.isFinite(Number(result.lon)) && result.display_name &&
            (!result.countrycode || result.countrycode.toLowerCase() === 'vn'));
        return reverse ? (matches[0] || null) : matches;
    };

    const lookup = async (endpoint, directPath, photonPath, reverse = false, focus = null) => {
        const focusPoint = validFocus(focus);
        let serverError;
        let serverHadNoMatches = false;
        try {
            const { response, result } = await fetchJson(`/api/geocoding/${endpoint}${focusQuery(focusPoint)}`, {
                headers: { 'Accept': 'application/json' }
            });
            if (response.ok && hasResults(result, reverse)) return result;
            if (response.ok) {
                serverHadNoMatches = true;
                serverError = 'Không tìm thấy địa chỉ phù hợp.';
            } else {
                serverError = result?.message || 'Dịch vụ tìm địa chỉ không phản hồi.';
                if ([400, 422].includes(response.status)) throw new Error(serverError);
            }
        } catch (error) {
            if (!(error instanceof TypeError) && error?.name !== 'AbortError') throw error;
            serverError = error?.name === 'AbortError'
                ? 'Máy chủ tìm địa chỉ phản hồi quá lâu.'
                : 'Máy chủ tìm địa chỉ không phản hồi.';
        }

        let directError;
        if (!serverHadNoMatches) {
            try {
                const result = await directLookup(appendViewbox(directPath, focusPoint));
                if (hasResults(result, reverse)) return result;
            } catch (error) {
                directError = error;
            }
        }

        try {
            const result = await photonLookup(photonPath(focusPoint), reverse);
            if (hasResults(result, reverse)) return result;
            return reverse ? null : [];
        } catch (error) {
            const reason = [serverError, directError?.message].filter(Boolean).join(' ');
            const fallbackFailure = 'Trình duyệt cũng không kết nối được dịch vụ địa chỉ.';
            throw new Error(`${reason || error?.message || 'Không tìm thấy địa chỉ.'} ${fallbackFailure}`);
        }
    };

    window.SmartCarDeliveryGeocoding = {
        search: (query, focus) => {
            const point = validFocus(focus);
            const photonBias = point
                ? `&lat=${encodeURIComponent(point.lat)}&lon=${encodeURIComponent(point.lon)}&zoom=13&location_bias_scale=0.2`
                : '';
            return lookup(
                `search?q=${encodeURIComponent(query)}`,
                `search?format=jsonv2&limit=8&countrycodes=vn&q=${encodeURIComponent(query)}`,
                () => `api?limit=5&lang=vi&countrycode=VN${photonBias}&q=${encodeURIComponent(query)}`,
                false,
                point);
        },
        reverse: (lat, lon) => lookup(
            `reverse?lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}`,
            `reverse?format=jsonv2&addressdetails=1&zoom=18&accept-language=vi&lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}`,
            () => `reverse?limit=1&radius=0.1&lang=vi&lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}`,
            true)
    };
})();
