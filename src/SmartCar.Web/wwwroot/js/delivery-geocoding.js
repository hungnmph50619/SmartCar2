(() => {
    // Primary lookup goes through the app server (Nominatim). If that path is
    // unavailable, try Nominatim directly, then a different provider (Photon).
    // This avoids treating two network paths to the same provider as a real fallback.
    let nextDirectRequestAt = 0;
    let directQueue = Promise.resolve();

    const directNominatimLookup = path => {
        const result = directQueue.then(async () => {
            const delay = Math.max(0, nextDirectRequestAt - Date.now());
            if (delay) await new Promise(resolve => setTimeout(resolve, delay));
            nextDirectRequestAt = Date.now() + 1100;
            const response = await fetch(`https://nominatim.openstreetmap.org/${path}`, {
                headers: { 'Accept': 'application/json' },
                referrerPolicy: 'origin'
            });
            if (!response.ok) throw new Error('Không thể tra địa chỉ trực tiếp từ Nominatim.');
            return response.json();
        });
        directQueue = result.catch(() => {});
        return result;
    };

    const photonDisplayName = properties => {
        const values = [
            properties?.name,
            properties?.street,
            properties?.district,
            properties?.city,
            properties?.county,
            properties?.state,
            properties?.country
        ].filter(Boolean);
        return [...new Set(values)].join(', ');
    };

    const normalizePhoton = payload => {
        const features = Array.isArray(payload?.features) ? payload.features : [];
        return features
            .map(feature => {
                const coordinates = feature?.geometry?.coordinates;
                if (!Array.isArray(coordinates) || coordinates.length < 2) return null;
                const lon = Number(coordinates[0]);
                const lat = Number(coordinates[1]);
                if (!Number.isFinite(lat) || !Number.isFinite(lon)) return null;
                return {
                    lat,
                    lon,
                    display_name: photonDisplayName(feature.properties) || `${lat.toFixed(6)}, ${lon.toFixed(6)}`
                };
            })
            .filter(Boolean);
    };

    const photonLookup = async url => {
        const response = await fetch(url, {
            headers: { 'Accept': 'application/json' },
            referrerPolicy: 'origin'
        });
        if (!response.ok) throw new Error('Nguồn địa chỉ dự phòng không phản hồi.');
        return normalizePhoton(await response.json());
    };

    const lookup = async (endpoint, directPath, photonUrl, reverse = false) => {
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
            return await directNominatimLookup(directPath);
        } catch {
            try {
                const fallback = await photonLookup(photonUrl);
                return reverse ? (fallback[0] || null) : fallback;
            } catch {
                throw new Error(
                    `${serverError} Cả hai nguồn địa chỉ dự phòng trên trình duyệt đều không kết nối được.`);
            }
        }
    };

    window.SmartCarDeliveryGeocoding = {
        search: query => lookup(
            `search?q=${encodeURIComponent(query)}`,
            `search?format=jsonv2&limit=1&countrycodes=vn&q=${encodeURIComponent(query)}`,
            `https://photon.komoot.io/api/?limit=1&lang=vi&q=${encodeURIComponent(query)}`),
        reverse: (lat, lon) => lookup(
            `reverse?lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}`,
            `reverse?format=jsonv2&addressdetails=1&zoom=18&accept-language=vi&lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}`,
            `https://photon.komoot.io/reverse?limit=1&lang=vi&lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}`,
            true)
    };
})();
