namespace SerCapture.Ser {
    /// <summary>
    /// ColorID values stored at offset 18 of the SER header.
    /// Numeric values match the SER specification and Siril's <c>ser_color</c> enum
    /// (src/io/ser.h): MONO=0, RGGB=8, GRBG=9, GBRG=10, BGGR=11.
    /// </summary>
    public enum SerColorId {
        Mono = 0,
        BayerRggb = 8,
        BayerGrbg = 9,
        BayerGbrg = 10,
        BayerBggr = 11,
    }
}
