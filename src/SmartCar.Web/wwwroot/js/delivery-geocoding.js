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

    const photonLookup = async (path, reverse) => {
        const response = await fetch(`https://photon.komoot.io/${path}`, {
            headers: { 'Accept': 'application/json' },
            referrerPolicy: 'origin'
        });
        if (!response.ok) throw new Error('Dịch vụ địa chỉ dự phòng không phản hồi.');
        const data = await response.json();
        const matches = (data.features || []).map(feature => {
            const [lon, lat] = feature.geometry?.coordinates || [];
            const properties = feature.properties || {};
            const display_name = [properties.name, properties.street, properties.district,
                properties.city, properties.state, properties.country]
                .filter((part, index, parts) => part && parts.indexOf(part) === index)
                .join(', ');
            return { lat: String(lat), lon: String(lon), display_name };
        }).filter(result => Number.isFinite(Number(result.lat)) &&
            Number.isFinite(Number(result.lon)) && result.display_name);
        return reverse ? (matches[0] || null) : matches;
    };

    const lookup = async (endpoint, directPath, photonPath, reverse = false) => {
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
            try {
                return await photonLookup(photonPath, reverse);
            } catch {
                throw new Error(`${serverError} Trình duyệt cũng không kết nối được dịch vụ địa chỉ.`);
            }
        }
    };

    window.SmartCarDeliveryGeocoding = {
        search: query => lookup(
            `search?q=${encodeURIComponent(query)}`,
            `search?format=jsonv2&limit=1&countrycodes=vn&q=${encodeURIComponent(query)}`,
            `api?limit=1&lang=vi&countrycode=VN&q=${encodeURIComponent(query)}`),
        reverse: (lat, lon) => lookup(
            `reverse?lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}`,
            `reverse?format=jsonv2&addressdetails=1&zoom=18&accept-language=vi&lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}`,
            `reverse?limit=1&lang=vi&lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}`,
            true)
    };
})();
