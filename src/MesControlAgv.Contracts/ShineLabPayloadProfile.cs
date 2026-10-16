namespace MesControlAgv.Contracts;

/// <summary>Explicit vendor payload dialect; never inferred from an equipment code.</summary>
public enum ShineLabPayloadProfile
{
    Rike = 0,
    DcSeq = 1,
    DcSeqSingleChannel = 2
}
