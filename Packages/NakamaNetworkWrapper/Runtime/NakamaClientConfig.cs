using UnityEngine;

[CreateAssetMenu(menuName="Nakama/Client Config")]
public class NakamaClientConfig : ScriptableObject
{
    public string Host = "127.0.0.1";
    public int Port = 7350;
    public string Scheme = "http";
    public string ServerKey = "defaultkey";
}