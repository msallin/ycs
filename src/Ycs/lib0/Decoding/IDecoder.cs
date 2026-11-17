namespace Ycs;

internal interface IDecoder<T>
{
    T Read();
}
