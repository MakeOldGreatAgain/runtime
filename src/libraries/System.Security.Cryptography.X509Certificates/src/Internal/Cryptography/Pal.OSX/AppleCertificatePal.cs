// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Apple;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Internal.Cryptography.Pal
{
    internal sealed partial class AppleCertificatePal : ICertificatePal
    {
        private SafeSecIdentityHandle? _identityHandle;
        private SafeSecCertificateHandle _certHandle;
        private CertificateData _certData;
        private bool _readCertData;
        private SafeKeychainHandle? _tempKeychain;

        public static ICertificatePal? FromHandle(IntPtr handle)
        {
            return FromHandle(handle, true);
        }

        internal static ICertificatePal? FromHandle(IntPtr handle, bool throwOnFail)
        {
            if (handle == IntPtr.Zero)
                throw new ArgumentException(SR.Arg_InvalidHandle, nameof(handle));

            SafeSecCertificateHandle certHandle;
            SafeSecIdentityHandle identityHandle;

            if (Interop.AppleCrypto.X509DemuxAndRetainHandle(handle, out certHandle, out identityHandle))
            {
                Debug.Assert(
                    certHandle.IsInvalid != identityHandle.IsInvalid,
                    $"certHandle.IsInvalid ({certHandle.IsInvalid}) should differ from identityHandle.IsInvalid ({identityHandle.IsInvalid})");

                if (certHandle.IsInvalid)
                {
                    certHandle.Dispose();
                    return new AppleCertificatePal(identityHandle);
                }

                identityHandle.Dispose();
                return new AppleCertificatePal(certHandle);
            }

            certHandle.Dispose();
            identityHandle.Dispose();

            if (throwOnFail)
            {
                throw new ArgumentException(SR.Arg_InvalidHandle, nameof(handle));
            }

            return null;
        }

        public static ICertificatePal? FromOtherCert(X509Certificate cert)
        {
            Debug.Assert(cert.Pal != null);

            ICertificatePal? pal = FromHandle(cert.Handle);
            GC.KeepAlive(cert); // ensure cert's safe handle isn't finalized while raw handle is in use
            return pal;
        }

        public static ICertificatePal FromBlob(
            ReadOnlySpan<byte> rawData,
            SafePasswordHandle password,
            X509KeyStorageFlags keyStorageFlags)
        {
            Debug.Assert(password != null);

            X509ContentType contentType = X509Certificate2.GetCertContentType(rawData);

            if (contentType == X509ContentType.Pkcs7)
            {
                // In single mode for a PKCS#7 signed or signed-and-enveloped file we're supposed to return
                // the certificate which signed the PKCS#7 file.
                //
                // X509Certificate2Collection::Export(X509ContentType.Pkcs7) claims to be a signed PKCS#7,
                // but doesn't emit a signature block. So this is hard to test.
                //
                // TODO(2910): Figure out how to extract the signing certificate, when it's present.
                throw new CryptographicException(SR.Cryptography_X509_PKCS7_NoSigner);
            }

            if (contentType == X509ContentType.Pkcs12)
            {
                if ((keyStorageFlags & X509KeyStorageFlags.EphemeralKeySet) == X509KeyStorageFlags.EphemeralKeySet)
                {
                    throw new PlatformNotSupportedException(SR.Cryptography_X509_NoEphemeralPfx);
                }

                bool exportable = (keyStorageFlags & X509KeyStorageFlags.Exportable) == X509KeyStorageFlags.Exportable;

                bool persist =
                    (keyStorageFlags & X509KeyStorageFlags.PersistKeySet) == X509KeyStorageFlags.PersistKeySet;

                SafeKeychainHandle keychain = persist
                    ? Interop.AppleCrypto.SecKeychainCopyDefault()
                    : Interop.AppleCrypto.CreateTemporaryKeychain();

                using (keychain)
                {
                    AppleCertificatePal ret = ImportPkcs12(rawData, password, exportable, keychain);
                    if (!persist)
                    {
                        // If we used temporary keychain we need to prevent deletion.
                        // on 10.15+ if keychain is unlinked, certain certificate operations may fail.
                        bool success = false;
                        keychain.DangerousAddRef(ref success);
                        if (success)
                        {
                            ret._tempKeychain = keychain;
                        }
                    }

                    return ret;
                }
            }

            SafeSecIdentityHandle identityHandle;
            SafeSecCertificateHandle certHandle = Interop.AppleCrypto.X509ImportCertificate(
                rawData,
                contentType,
                SafePasswordHandle.InvalidHandle,
                SafeTemporaryKeychainHandle.InvalidHandle,
                exportable: true,
                out identityHandle);

            if (identityHandle.IsInvalid)
            {
                identityHandle.Dispose();
                return new AppleCertificatePal(certHandle);
            }

            Debug.Fail("Non-PKCS12 import produced an identity handle");

            identityHandle.Dispose();
            certHandle.Dispose();
            throw new CryptographicException();
        }

        public static ICertificatePal FromFile(string fileName, SafePasswordHandle password, X509KeyStorageFlags keyStorageFlags)
        {
            Debug.Assert(password != null);

            byte[] fileBytes = System.IO.File.ReadAllBytes(fileName);
            return FromBlob(fileBytes, password, keyStorageFlags);
        }

        internal AppleCertificatePal(SafeSecCertificateHandle certHandle)
        {
            Debug.Assert(!certHandle.IsInvalid);

            _certHandle = certHandle;
        }

        internal AppleCertificatePal(SafeSecIdentityHandle identityHandle)
        {
            Debug.Assert(!identityHandle.IsInvalid);

            _identityHandle = identityHandle;
            _certHandle = Interop.AppleCrypto.X509GetCertFromIdentity(identityHandle);
        }

        public void Dispose()
        {
            _certHandle?.Dispose();
            _identityHandle?.Dispose();

            _certHandle = null!;
            _identityHandle = null;

            SafeKeychainHandle? tempKeychain = Interlocked.Exchange(ref _tempKeychain, null);
            if (tempKeychain != null)
            {
                tempKeychain.Dispose();
            }
        }

        internal SafeSecCertificateHandle CertificateHandle => _certHandle;
        internal SafeSecIdentityHandle? IdentityHandle => _identityHandle;

        public bool HasPrivateKey => !(_identityHandle?.IsInvalid ?? true);

        public IntPtr Handle
        {
            get
            {
                if (HasPrivateKey)
                {
                    return _identityHandle!.DangerousGetHandle();
                }

                return _certHandle?.DangerousGetHandle() ?? IntPtr.Zero;
            }
        }

        public string Issuer
        {
            get
            {
                EnsureCertData();
                return _certData.IssuerName;
            }
        }

        public string Subject
        {
            get
            {
                EnsureCertData();
                return _certData.SubjectName;
            }
        }

        public string LegacyIssuer => IssuerName.Decode(X500DistinguishedNameFlags.None);

        public string LegacySubject => SubjectName.Decode(X500DistinguishedNameFlags.None);

        public string KeyAlgorithm
        {
            get
            {
                EnsureCertData();
                return _certData.PublicKeyAlgorithm.AlgorithmId!;
            }
        }

        public byte[] KeyAlgorithmParameters
        {
            get
            {
                EnsureCertData();
                return _certData.PublicKeyAlgorithm.Parameters;
            }
        }

        public byte[] PublicKeyValue
        {
            get
            {
                EnsureCertData();
                return _certData.PublicKey;
            }
        }

        public byte[] SerialNumber
        {
            get
            {
                EnsureCertData();
                return _certData.SerialNumber;
            }
        }

        public string SignatureAlgorithm
        {
            get
            {
                EnsureCertData();
                return _certData.SignatureAlgorithm.AlgorithmId!;
            }
        }

        public string FriendlyName
        {
            get { return ""; }
            set
            {
                throw new PlatformNotSupportedException(
                    SR.Format(SR.Cryptography_Unix_X509_PropertyNotSettable, nameof(FriendlyName)));
            }
        }

        public int Version
        {
            get
            {
                EnsureCertData();
                return _certData.Version + 1;
            }
        }

        public X500DistinguishedName SubjectName
        {
            get
            {
                EnsureCertData();
                return _certData.Subject;
            }
        }

        public X500DistinguishedName IssuerName
        {
            get
            {
                EnsureCertData();
                return _certData.Issuer;
            }
        }

        public PolicyData GetPolicyData()
        {
            PolicyData policyData = default;
            EnsureCertData();

            foreach (X509Extension extension in _certData.Extensions)
            {
                switch (extension.Oid!.Value)
                {
                    case Oids.ApplicationCertPolicies:
                        policyData.ApplicationCertPolicies = extension.RawData;
                        break;
                    case Oids.CertPolicies:
                        policyData.CertPolicies = extension.RawData;
                        break;
                    case Oids.CertPolicyMappings:
                        policyData.CertPolicyMappings = extension.RawData;
                        break;
                    case Oids.CertPolicyConstraints:
                        policyData.CertPolicyConstraints = extension.RawData;
                        break;
                    case Oids.EnhancedKeyUsage:
                        policyData.EnhancedKeyUsage = extension.RawData;
                        break;
                    case Oids.InhibitAnyPolicyExtension:
                        policyData.InhibitAnyPolicyExtension = extension.RawData;
                        break;
                }
            }

            return policyData;
        }

        public IEnumerable<X509Extension> Extensions {
            get
            {
                EnsureCertData();
                return _certData.Extensions;
            }
        }

        public byte[] RawData
        {
            get
            {
                EnsureCertData();
                return _certData.RawData.CloneByteArray();
            }
        }

        public DateTime NotAfter
        {
            get
            {
                EnsureCertData();
                return _certData.NotAfter.ToLocalTime();
            }
        }

        public DateTime NotBefore
        {
            get
            {
                EnsureCertData();
                return _certData.NotBefore.ToLocalTime();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA5350", Justification = "SHA1 is required for Compat")]
        public byte[] Thumbprint
        {
            get
            {
                EnsureCertData();
                return SHA1.HashData(_certData.RawData);
            }
        }

        public bool Archived
        {
            get { return false; }
            set
            {
                throw new PlatformNotSupportedException(
                    SR.Format(SR.Cryptography_Unix_X509_PropertyNotSettable, nameof(Archived)));
            }
        }

        public byte[] SubjectPublicKeyInfo
        {
            get
            {
                EnsureCertData();

                return _certData.SubjectPublicKeyInfo;
            }
        }

        internal unsafe byte[] ExportPkcs8(ReadOnlySpan<char> password)
        {
            Debug.Assert(_identityHandle != null);

            using (SafeSecKeyRefHandle key = Interop.AppleCrypto.X509GetPrivateKeyFromIdentity(_identityHandle))
            {
                return ExportPkcs8(key, password);
            }
        }

        internal static unsafe byte[] ExportPkcs8(SafeSecKeyRefHandle key, ReadOnlySpan<char> password)
        {
            using (SafeCFDataHandle data = Interop.AppleCrypto.SecKeyExportData(key, exportPrivate: true, password))
            {
                ReadOnlySpan<byte> systemExport = Interop.CoreFoundation.CFDataDangerousGetSpan(data);

                fixed (byte* ptr = systemExport)
                {
                    using (PointerMemoryManager<byte> manager = new PointerMemoryManager<byte>(ptr, systemExport.Length))
                    {
                        // Apple's PKCS8 export exports using PBES2, which Win7, Win8.1, and Apple all fail to
                        // understand in their PKCS12 readers, so re-encrypt using the Win7 PKCS12-PBE parameters.
                        //
                        // Since Apple only reliably exports keys with encrypted PKCS#8 there's not a
                        // "so export it plaintext and only encrypt it once" option.
                        AsnWriter writer = KeyFormatHelper.ReencryptPkcs8(
                            password,
                            manager.Memory,
                            password,
                            UnixExportProvider.s_windowsPbe);

                        return writer.Encode();
                    }
                }
            }
        }

        public RSA? GetRSAPrivateKey()
        {
            if (_identityHandle == null)
                return null;

            Debug.Assert(!_identityHandle.IsInvalid);
            SafeSecKeyRefHandle publicKey = Interop.AppleCrypto.X509GetPublicKey(_certHandle);
            SafeSecKeyRefHandle privateKey = Interop.AppleCrypto.X509GetPrivateKeyFromIdentity(_identityHandle);
            Debug.Assert(!publicKey.IsInvalid);

            return new RSAImplementation.RSASecurityTransforms(publicKey, privateKey);
        }

        public DSA? GetDSAPrivateKey()
        {
            if (_identityHandle == null)
                return null;

            Debug.Assert(!_identityHandle.IsInvalid);
            SafeSecKeyRefHandle publicKey = Interop.AppleCrypto.X509GetPublicKey(_certHandle);
            SafeSecKeyRefHandle privateKey = Interop.AppleCrypto.X509GetPrivateKeyFromIdentity(_identityHandle);

            if (publicKey.IsInvalid)
            {
                // SecCertificateCopyKey returns null for DSA, so fall back to manually building it.
                publicKey = Interop.AppleCrypto.ImportEphemeralKey(_certData.SubjectPublicKeyInfo, false);
            }

            return new DSAImplementation.DSASecurityTransforms(publicKey, privateKey);
        }

        public ECDsa? GetECDsaPrivateKey()
        {
            if (_identityHandle == null)
                return null;

            Debug.Assert(!_identityHandle.IsInvalid);
            SafeSecKeyRefHandle publicKey = Interop.AppleCrypto.X509GetPublicKey(_certHandle);
            SafeSecKeyRefHandle privateKey = Interop.AppleCrypto.X509GetPrivateKeyFromIdentity(_identityHandle);
            Debug.Assert(!publicKey.IsInvalid);

            return new ECDsaImplementation.ECDsaSecurityTransforms(publicKey, privateKey);
        }

        public ICertificatePal CopyWithPrivateKey(DSA privateKey)
        {
            var typedKey = privateKey as DSAImplementation.DSASecurityTransforms;

            if (typedKey != null)
            {
                return CopyWithPrivateKey(typedKey.GetKeys());
            }

            DSAParameters dsaParameters = privateKey.ExportParameters(true);

            using (PinAndClear.Track(dsaParameters.X!))
            using (typedKey = new DSAImplementation.DSASecurityTransforms())
            {
                typedKey.ImportParameters(dsaParameters);
                return CopyWithPrivateKey(typedKey.GetKeys());
            }
        }

        public ICertificatePal CopyWithPrivateKey(ECDsa privateKey)
        {
            var typedKey = privateKey as ECDsaImplementation.ECDsaSecurityTransforms;

            if (typedKey != null)
            {
                return CopyWithPrivateKey(typedKey.GetKeys());
            }

            ECParameters ecParameters = privateKey.ExportParameters(true);

            using (PinAndClear.Track(ecParameters.D!))
            using (typedKey = new ECDsaImplementation.ECDsaSecurityTransforms())
            {
                typedKey.ImportParameters(ecParameters);
                return CopyWithPrivateKey(typedKey.GetKeys());
            }
        }

        public ICertificatePal CopyWithPrivateKey(RSA privateKey)
        {
            var typedKey = privateKey as RSAImplementation.RSASecurityTransforms;

            if (typedKey != null)
            {
                return CopyWithPrivateKey(typedKey.GetKeys());
            }

            RSAParameters rsaParameters = privateKey.ExportParameters(true);

            using (PinAndClear.Track(rsaParameters.D!))
            using (PinAndClear.Track(rsaParameters.P!))
            using (PinAndClear.Track(rsaParameters.Q!))
            using (PinAndClear.Track(rsaParameters.DP!))
            using (PinAndClear.Track(rsaParameters.DQ!))
            using (PinAndClear.Track(rsaParameters.InverseQ!))
            using (typedKey = new RSAImplementation.RSASecurityTransforms())
            {
                typedKey.ImportParameters(rsaParameters);
                return CopyWithPrivateKey(typedKey.GetKeys());
            }
        }

        internal AppleCertificatePal? MoveToKeychain(SafeKeychainHandle keychain, SafeSecKeyRefHandle? privateKey)
        {
            SafeSecIdentityHandle? identity = Interop.AppleCrypto.X509MoveToKeychain(
                _certHandle,
                keychain,
                privateKey);

            if (identity != null)
            {
                return new AppleCertificatePal(identity);
            }

            return null;
        }

        private ICertificatePal CopyWithPrivateKey(SecKeyPair keyPair)
        {
            if (keyPair.PrivateKey == null)
            {
                // Both Windows and Linux/OpenSSL are unaware if they bound a public or private key.
                // Here, we do know.  So throw if we can't do what they asked.
                throw new CryptographicException(SR.Cryptography_CSP_NoPrivateKey);
            }

            SafeKeychainHandle keychain = Interop.AppleCrypto.SecKeychainItemCopyKeychain(keyPair.PrivateKey);

            // If we're using a key already in a keychain don't add the certificate to that keychain here,
            // do it in the temporary add/remove in the shim.
            SafeKeychainHandle cloneKeychain = SafeTemporaryKeychainHandle.InvalidHandle;

            if (keychain.IsInvalid)
            {
                keychain = Interop.AppleCrypto.CreateTemporaryKeychain();
                cloneKeychain = keychain;
            }

            // Because SecIdentityRef only has private constructors we need to have the cert and the key
            // in the same keychain.  That almost certainly means we're going to need to add this cert to a
            // keychain, and when a cert that isn't part of a keychain gets added to a keychain then the
            // interior pointer of "what keychain did I come from?" used by SecKeychainItemCopyKeychain gets
            // set. That makes this function have side effects, which is not desired.
            //
            // It also makes reference tracking on temporary keychains broken, since the cert can
            // DangerousRelease a handle it didn't DangerousAddRef on.  And so CopyWithPrivateKey makes
            // a temporary keychain, then deletes it before anyone has a chance to (e.g.) export the
            // new identity as a PKCS#12 blob.
            //
            // Solution: Clone the cert, like we do in Windows.
            SafeSecCertificateHandle tempHandle;

            {
                byte[] export = RawData;
                const bool exportable = false;
                SafeSecIdentityHandle identityHandle;
                tempHandle = Interop.AppleCrypto.X509ImportCertificate(
                    export,
                    X509ContentType.Cert,
                    SafePasswordHandle.InvalidHandle,
                    cloneKeychain,
                    exportable,
                    out identityHandle);

                Debug.Assert(identityHandle.IsInvalid, "identityHandle should be IsInvalid");
                identityHandle.Dispose();

                Debug.Assert(!tempHandle.IsInvalid, "tempHandle should not be IsInvalid");
            }

            using (keychain)
            using (tempHandle)
            {
                SafeSecIdentityHandle identityHandle = Interop.AppleCrypto.X509CopyWithPrivateKey(
                    tempHandle,
                    keyPair.PrivateKey,
                    keychain);

                AppleCertificatePal newPal = new AppleCertificatePal(identityHandle);
                return newPal;
            }
        }

        public string GetNameInfo(X509NameType nameType, bool forIssuer)
        {
            EnsureCertData();
            return _certData.GetNameInfo(nameType, forIssuer);
        }

        public void AppendPrivateKeyInfo(StringBuilder sb)
        {
            if (!HasPrivateKey)
            {
                return;
            }

            // There's nothing really to say about the key, just acknowledge there is one.
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("[Private Key]");
        }

        public byte[] Export(X509ContentType contentType, SafePasswordHandle password)
        {
            using (IExportPal storePal = StorePal.FromCertificate(this))
            {
                byte[]? exported = storePal.Export(contentType, password);
                Debug.Assert(exported != null);
                return exported;
            }
        }

        private void EnsureCertData()
        {
            if (_readCertData)
                return;

            Debug.Assert(!_certHandle.IsInvalid);

            try
            {
                _certData = new CertificateData(Interop.AppleCrypto.X509GetRawData(_certHandle));
            }
            catch (CryptographicException e)
            {
                string? subjectSummary = Interop.AppleCrypto.X509GetSubjectSummary(_certHandle);

                if (subjectSummary is null)
                {
                    throw;
                }

                string message = SR.Format(
                    SR.Cryptography_X509_CertificateCorrupted,
                    subjectSummary);

                throw new CryptographicException(message, e);
            }

            _readCertData = true;
        }

    }
}
